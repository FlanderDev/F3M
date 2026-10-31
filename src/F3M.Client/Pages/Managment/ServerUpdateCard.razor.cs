using FlanderDev.RouteGen.Abstractions;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using System.Text.Json;

namespace F3M.Client.Pages.Managment;

public partial class ServerUpdateCard
{
    // Often while something happens, rarely otherwise; a restart takes about a minute.
    private static readonly TimeSpan BusyPoll = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(30);

    // How long before the page says something looks stuck.
    private static readonly TimeSpan HostSlow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RestartSlow = TimeSpan.FromMinutes(3);

    /// <summary>What update.sh (and backup.sh) announce with "== ... ==" lines in the log, in order.</summary>
    private static readonly UpdateStep[] Steps =
    [
        new("Looking for updates", "Get updates", "bi-search"),
        new("Backing up", "Backup", "bi-archive"),
        new("Pulling", "New code", "bi-download"),
        new("Rebuilding", "Rebuild", "bi-hammer"),
        new("Waiting for the server", "Start", "bi-power")
    ];

    private sealed record UpdateStep(string LogPrefix, string Label, string Icon);

    private enum StepState { Pending, Skipped, Current, Done, Failed }

    [Inject] private IJSRuntime JS { get; set; } = default!;

    private ServerUpdateStatus? status;
    private bool loading = true;
    private bool requesting;
    private bool confirming;
    private bool restarting;
    private string? error;

    // The version this page was loaded with; when the server's changes, the update went through.
    private string? loadedCommit;
    private string? updatedTo;

    // When the page first saw a request waiting for the host, or the server gone; for the timers and hints.
    private DateTime? waitingSince;
    private DateTime? restartingSince;

    // A check that just finished, so the buttons can say how it went for a moment.
    private DateTime? checkDoneAt;

    private ElementReference logBox;
    private string? scrolledLog;

    private readonly CancellationTokenSource stop = new();

    // Cut the current wait short, so a request is followed closely right away.
    private CancellationTokenSource? wake;

    /// <summary>An update is asked for or under way, or the server is gone for its restart.</summary>
    private bool Busy => restarting || status is { UpdateRequested: true } or { State: ServerUpdateState.Running };

    protected override async Task OnInitializedAsync()
    {
        await RefreshAsync();
        loadedCommit = status?.RunningCommit;
        _ = PollAsync();
        _ = TickAsync();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // New output while it runs: keep the log at its end, unless it was scrolled up.
        if (status is { State: ServerUpdateState.Running, Log: { } log } && log != scrolledLog)
        {
            scrolledLog = log;
            try
            {
                await JS.InvokeVoidAsync("f3m.followEnd", logBox);
            }
            catch (JSException)
            {
                // No log box rendered (closed details); nothing to scroll.
            }
        }
    }

    private async Task PollAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using (wake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
                {
                    try
                    {
                        await Task.Delay(Busy || status is { CheckRequested: true } ? BusyPoll : IdlePoll, wake.Token);
                    }
                    catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                    {
                        // Woken by a request.
                    }
                }
                wake = null;
                await RefreshAsync();
                StateHasChanged();
            }
        }
        catch (OperationCanceledException)
        {
            // The page was left.
        }
    }

    // Keeps the timers moving between polls.
    private async Task TickAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stop.Token);
                if (checkDoneAt is { } at && DateTime.UtcNow - at > TimeSpan.FromSeconds(8))
                    checkDoneAt = null;
                else if (!Busy && waitingSince is null && checkDoneAt is null)
                    continue;
                StateHasChanged();
            }
        }
        catch (OperationCanceledException)
        {
            // The page was left.
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var wasBusy = Busy;
            var wasChecking = status is { CheckRequested: true };
            status = await Api.GetStatusAsync(stop.Token);
            restarting = false;
            restartingSince = null;
            if (wasBusy || error is not null)
                error = null;

            if (wasChecking && !status.CheckRequested && status.CheckError is null)
                checkDoneAt = DateTime.UtcNow;
            TrackWaiting();

            if (loadedCommit is not null && status.RunningCommit is { } running && !SameCommit(running, loadedCommit))
            {
                updatedTo = running;
                confirming = false;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException) when (Busy)
        {
            // The container is being replaced; keep asking until the new one answers.
            StartRestarting();
        }
        catch (ApiException ex) when (Busy && (int)ex.StatusCode >= 500)
        {
            StartRestarting();
        }
        catch (Exception ex)
        {
            error = $"The update status could not be loaded: {Describe(ex)}";
        }
        finally
        {
            loading = false;
        }
    }

    private async Task CheckNow()
    {
        error = null;
        requesting = true;
        try
        {
            status = await Api.RequestCheckAsync();
            checkDoneAt = null;
            TrackWaiting();
            wake?.Cancel();
        }
        catch (Exception ex)
        {
            error = Describe(ex);
        }
        finally
        {
            requesting = false;
        }
    }

    private async Task UpdateNow()
    {
        error = null;
        requesting = true;
        try
        {
            status = await Api.RequestUpdateAsync();
            confirming = false;
            TrackWaiting();
            wake?.Cancel();
        }
        catch (Exception ex)
        {
            error = Describe(ex);
        }
        finally
        {
            requesting = false;
        }
    }

    private void StartRestarting()
    {
        restarting = true;
        restartingSince ??= DateTime.UtcNow;
    }

    /// <summary>Starts the waiting timer when a request sits there for the host, and stops it once the host took it.</summary>
    private void TrackWaiting()
    {
        var waiting = status is { UpdateRequested: true } or { CheckRequested: true, State: not ServerUpdateState.Running };
        waitingSince = waiting ? waitingSince ?? DateTime.UtcNow : null;
    }

    /// <summary>The host hasn't picked up a request for a while; its service may not be running.</summary>
    private bool HostSeemsSlow => waitingSince is { } since && DateTime.UtcNow - since > HostSlow;

    private bool RestartSeemsSlow => restartingSince is { } since && DateTime.UtcNow - since > RestartSlow;

    /// <summary>The steps show while an update is under way, and after one failed.</summary>
    private bool ShowProgress => Busy || (status is { State: ServerUpdateState.Failed } && ReachedStep() >= 0);

    /// <summary>The last step the log announced; -1 when none yet.</summary>
    private int ReachedStep()
    {
        if (status is not { UpdateRequested: false, Log: { Length: > 0 } log })
            return -1;

        var reached = -1;
        foreach (var line in log.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length < 6 || !text.StartsWith("== ", StringComparison.Ordinal) || !text.EndsWith(" ==", StringComparison.Ordinal))
                continue;
            var name = text[3..^3];
            reached = Math.Max(reached, Array.FindIndex(Steps, s => name.StartsWith(s.LogPrefix, StringComparison.OrdinalIgnoreCase)));
        }
        return reached;
    }

    private IEnumerable<(UpdateStep Step, StepState State)> Progress()
    {
        var reached = ReachedStep();
        var log = status?.Log ?? string.Empty;
        var failed = status is { State: ServerUpdateState.Failed } && !Busy;

        for (var i = 0; i < Steps.Length; i++)
        {
            var state = i > reached ? StepState.Pending
                : i < reached ? log.Contains("== " + Steps[i].LogPrefix, StringComparison.OrdinalIgnoreCase) ? StepState.Done : StepState.Skipped
                : failed ? StepState.Failed
                : StepState.Current;
            yield return (Steps[i], state);
        }
    }

    private string ProgressTitle => status switch
    {
        _ when restarting => "The server is restarting with the new version",
        { UpdateRequested: true } => "Update requested, waiting for the server to start it",
        { State: ServerUpdateState.Running } => "Updating",
        _ => ReachedStep() is var step and >= 0 ? $"The update failed at: {Steps[step].Label}" : "The update failed"
    };

    /// <summary>How long the update has been going: from the host's start time, else since the request.</summary>
    private TimeSpan? Elapsed
    {
        get
        {
            if (!Busy)
                return null;
            var since = status is { UpdateRequested: false, StartedAt: { } started } ? started : waitingSince;
            return since is { } s && DateTime.UtcNow - s is var time && time > TimeSpan.Zero ? time : null;
        }
    }

    /// <summary>The last thing the update printed, without the dots the health check prints while it waits.</summary>
    private string? CurrentLine
    {
        get
        {
            if (restarting || status is not { State: ServerUpdateState.Running, UpdateRequested: false, Log: { Length: > 0 } log })
                return null;
            var line = log.Split('\n').Select(l => l.Trim().Trim('.').Trim()).LastOrDefault(l => l.Length > 0);
            return line is null || line.StartsWith("==", StringComparison.Ordinal) ? null : line;
        }
    }

    /// <summary>A failed update's duration, or a finished one's, for the line under the steps.</summary>
    private static string? Took(ServerUpdateStatus status) =>
        status is { StartedAt: { } started, FinishedAt: { } finished } && finished >= started ? Clock(finished - started) : null;

    private static string Clock(TimeSpan time) => $"{(int)time.TotalMinutes}:{time.Seconds:00}";

    private void Reload() => Navigation.NavigateTo(Navigation.Uri, forceLoad: true);

    private static string Short(string commit) => commit.Length > 7 ? commit[..7] : commit;

    /// <summary>The running version may carry a shortened commit; compare by the shorter one.</summary>
    private static bool SameCommit(string a, string b)
    {
        var length = Math.Min(a.Length, b.Length);
        return length >= 7 && string.Equals(a[..length], b[..length], StringComparison.OrdinalIgnoreCase);
    }

    private static string Ago(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
            : $"{(int)age.TotalDays} days ago";
    }

    /// <summary>The server's message for a refused request (in the response body), else the exception's.</summary>
    private static string Describe(Exception ex)
    {
        if (ex is not ApiException { ResponseBody: { Length: > 0 } body })
            return ex.Message;
        try
        {
            return JsonSerializer.Deserialize<string>(body) ?? body;
        }
        catch (JsonException)
        {
            return body;
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        stop.Dispose();
    }
}
