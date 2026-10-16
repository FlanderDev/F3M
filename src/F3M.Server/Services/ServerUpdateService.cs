using F3M.Shared;
using F3M.Shared.Api;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using System.Globalization;
using System.Reflection;

namespace F3M.Server.Services;

/// <summary>
/// Server-side implementation of <see cref="IServerUpdateApi"/>. The server can't update itself: it runs inside the
/// container it would replace, and gets no access to Docker. Instead it talks to the host service that
/// admin-updates.sh installs, through a folder in the storage volume (<see cref="ExchangeDirectory"/>):
/// <list type="bullet">
/// <item>the server leaves update-requested or check-requested there; the service only looks whether they exist;</item>
/// <item>the service writes check.txt (new commits), status.txt (the last update) and update.log there.</item>
/// </list>
/// </summary>
public sealed class ServerUpdateService(IHttpContextAccessor httpContextAccessor, ILogger<ServerUpdateService> logger) : IServerUpdateApi
{
    public static string ExchangeDirectory => Path.Combine(Assets.StorageRoot, "Updates");

    private static string UpdateRequest => Path.Combine(ExchangeDirectory, "update-requested");
    private static string CheckRequest => Path.Combine(ExchangeDirectory, "check-requested");
    private static string CheckFile => Path.Combine(ExchangeDirectory, "check.txt");
    private static string StatusFile => Path.Combine(ExchangeDirectory, "status.txt");
    private static string LogFile => Path.Combine(ExchangeDirectory, "update.log");

    private const int MaxLogLines = 300;
    private const int MaxLogBytes = 128 * 1024;
    private const int MaxReportBytes = 64 * 1024;

    public Task<ServerUpdateStatus> GetStatusAsync(CancellationToken ct = default) => Task.FromResult(ReadStatus());

    public Task<ServerUpdateStatus> RequestCheckAsync(CancellationToken ct = default)
    {
        var status = ReadStatus();
        if (!status.HelperInstalled)
            throw new InvalidOperationException(NotInstalled);

        if (!status.CheckRequested)
            Request(CheckRequest);
        return Task.FromResult(ReadStatus());
    }

    public Task<ServerUpdateStatus> RequestUpdateAsync(CancellationToken ct = default)
    {
        var status = ReadStatus();
        if (!status.HelperInstalled)
            throw new InvalidOperationException(NotInstalled);

        if (!status.UpdateRequested && status.State != ServerUpdateState.Running)
        {
            Request(UpdateRequest);
            logger.LogInformation("{Username} requested a server update from {Commit}. The server restarts when it is done.",
                httpContextAccessor.HttpContext?.User.Identity?.Name ?? "An admin", status.RunningCommit ?? "an unknown version");
        }
        return Task.FromResult(ReadStatus());
    }

    private const string NotInstalled = "Updating from the admin page isn't set up on this server. Run ./admin-updates.sh in the F3M folder on the server.";

    /// <summary>Only the file's existence counts; what is in it is for a person looking at the folder.</summary>
    private void Request(string path)
    {
        Directory.CreateDirectory(ExchangeDirectory);
        File.WriteAllText(path, $"Requested by {httpContextAccessor.HttpContext?.User.Identity?.Name ?? "an admin"} at {DateTime.UtcNow:O}{Environment.NewLine}");
    }

    private static ServerUpdateStatus ReadStatus()
    {
        var commit = typeof(ServerUpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "SourceCommit")?.Value;
        var status = new ServerUpdateStatus
        {
            RunningVersion = Configuration.BuildTimeStamp,
            RunningCommit = commit is { Length: >= 7 } ? commit : null,
            UpdateRequested = File.Exists(UpdateRequest),
            CheckRequested = File.Exists(CheckRequest)
        };

        var check = ReadReport(CheckFile);
        if (check is not null)
        {
            status.HelperInstalled = true;
            status.CheckedAt = ParseTime(check.Values.GetValueOrDefault("checked"));
            status.Branch = Blank(check.Values.GetValueOrDefault("branch"));
            status.Upstream = Blank(check.Values.GetValueOrDefault("upstream"));
            status.HeadCommit = Blank(check.Values.GetValueOrDefault("head"));
            status.CheckError = Blank(check.Values.GetValueOrDefault("error"));
            status.NewCommits = check.Commits;
        }

        var report = ReadReport(StatusFile);
        if (report is not null)
        {
            status.State = report.Values.GetValueOrDefault("state") switch
            {
                "running" => ServerUpdateState.Running,
                "succeeded" => ServerUpdateState.Succeeded,
                "failed" => ServerUpdateState.Failed,
                _ => ServerUpdateState.None
            };
            status.StartedAt = ParseTime(report.Values.GetValueOrDefault("started"));
            status.FinishedAt = ParseTime(report.Values.GetValueOrDefault("finished"));
            status.ExitCode = int.TryParse(report.Values.GetValueOrDefault("exit"), out var code) ? code : null;
            status.Log = ReadLogTail();
        }

        return status;
    }

    private sealed record Report(Dictionary<string, string> Values, List<PendingCommit> Commits);

    /// <summary>A key=value file from the host service; "commit=" lines are the new commits (sha, date, subject, tab-separated).</summary>
    private static Report? ReadReport(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxReportBytes)
                return null;

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var commits = new List<PendingCommit>();
            foreach (var line in File.ReadAllLines(path))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                var key = line[..separator];
                var value = line[(separator + 1)..];
                if (key == "commit")
                {
                    var parts = value.Split('\t', 3);
                    commits.Add(new PendingCommit
                    {
                        Sha = parts[0],
                        Date = parts.Length > 1 && DateTimeOffset.TryParse(parts[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
                        Subject = parts.Length > 2 ? parts[2] : string.Empty
                    });
                }
                else
                {
                    values[key] = value;
                }
            }
            return new Report(values, commits);
        }
        catch (IOException)
        {
            // Being replaced right now; the next poll reads it.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The last lines of the update's output (the start is the least interesting part of a long build).</summary>
    private static string? ReadLogTail()
    {
        try
        {
            using var stream = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxLogBytes)
                stream.Seek(-MaxLogBytes, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n');
            return string.Join('\n', lines.TakeLast(MaxLogLines)).TrimEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTime? ParseTime(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time) ? time : null;

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
