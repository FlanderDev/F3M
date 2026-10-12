using F3M.Server.Data;
using F3M.Shared;
using F3M.Shared.Api;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace F3M.Server.Services.Import;

/// <summary>
/// Downloads the community mods named in the import plan from F95zone and imports them, one step at a time, each
/// started by an admin on the import page. Steps run in the background; the page polls <see cref="GetStatusAsync"/>.
///
/// Everything lives in Storage/Import: the plan in use (a copy of the one shipped in Import/import-plan.json, or one
/// an admin uploaded), the downloads (files/&lt;mod key&gt;/&lt;version&gt;/&lt;file&gt;) and state.json with what went
/// wrong last per mod. Which versions are imported is read from the database (same name and author), so mods imported
/// with import/tools/upload.py count too.
///
/// Downloaded files are never run or extracted: the import only lists archives, like the upload form does.
/// </summary>
public sealed class F95ImportService(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    IHostApplicationLifetime lifetime,
    ILogger<F95ImportService> logger) : IImportApi
{
    public const string HttpClientName = "F3M.Import";

    private const string BundledPlanResource = "F3M.Server.Import.import-plan.json";
    private const int MaxLogLines = 400;

    /// <summary>Between two downloads, so F95zone is not hammered.</summary>
    private static readonly TimeSpan DownloadPause = TimeSpan.FromMilliseconds(500);

    private static readonly JsonSerializerOptions StateJson = new() { WriteIndented = true };

    private static string Root => Path.Combine(Assets.StorageRoot, "Import");
    private static string PlanPath => Path.Combine(Root, "import-plan.json");
    private static string StatePath => Path.Combine(Root, "state.json");
    private static string FilesRoot => Path.Combine(Root, "files");

    private readonly Lock gate = new();
    private readonly List<ImportLogLineDto> log = [];
    private ImportJobDto job = new();
    private CancellationTokenSource? cancel;
    private ImportState? state;
    private ImportPlan? plan;
    private string? planError;
    private bool planLoaded;

    // ── API ───────────────────────────────────────────────────────────────

    public async Task<ImportStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        var (currentPlan, error) = LoadPlan();
        var status = new ImportStatusDto();
        if (currentPlan is not null)
        {
            var imported = await ImportedVersionsAsync(currentPlan, ct);
            lock (gate)
            {
                foreach (var mod in currentPlan.Mods)
                    status.Mods.Add(Describe(mod, imported));
                status.Skipped = [.. currentPlan.Skipped.Select(s => new ImportSkippedDto { Key = s.Key, Name = s.Name ?? s.Key, Url = s.Url, Reason = s.Reason })];
            }
        }

        lock (gate)
        {
            status.Plan = PlanInfo(currentPlan, error);
            status.Job = Copy(job);
            status.Log = [.. log];
        }

        return status;
    }

    public Task<ImportJobDto> StartDownloadAsync(CancellationToken ct = default)
    {
        var currentPlan = RequirePlan();
        var work = DownloadWork(currentPlan.Mods);
        return Task.FromResult(Start(ImportJobKind.Download, work.Count, "Every file is on the server already.",
            token => RunDownloadAsync(work, token)));
    }

    public async Task<ImportJobDto> StartImportAsync(ImportStartDto dto, CancellationToken ct = default)
    {
        var currentPlan = RequirePlan();
        var only = dto.OnlyKeys is null ? null : new HashSet<string>(dto.OnlyKeys, StringComparer.OrdinalIgnoreCase);
        if (only?.FirstOrDefault(k => currentPlan.Mods.All(m => !m.Key.Equals(k, StringComparison.OrdinalIgnoreCase))) is { } unknown)
            throw new InvalidOperationException($"'{unknown}' is not in the plan.");

        // Mods that are fully on the site need no work; the step counts only the others.
        var imported = await ImportedVersionsAsync(currentPlan, ct);
        var work = currentPlan.InDependencyOrder()
            .Where(m => only is null || only.Contains(m.Key))
            .Where(m => m.Versions.Any(v => !IsImported(imported, m, v.Version)))
            .ToList();
        var accepted = new HashSet<string>(dto.AcceptWarningsFor, StringComparer.OrdinalIgnoreCase);

        return Start(ImportJobKind.Import, work.Count, "Every mod is imported already.",
            token => RunImportAsync(currentPlan, work, accepted, token));
    }

    public async Task<ImportJobDto> StartFrameworksAsync(CancellationToken ct = default)
    {
        var currentPlan = RequirePlan();
        var frameworks = currentPlan.Frameworks();
        if (frameworks.Count == 0)
            throw new InvalidOperationException("The import plan has none of the frameworks.");

        var imported = await ImportedVersionsAsync(currentPlan, ct);
        var mods = frameworks.Where(m => m.Versions.Any(v => !IsImported(imported, m, v.Version))).ToList();
        var downloads = DownloadWork(mods);

        return Start(ImportJobKind.Frameworks, downloads.Count + mods.Count, "Every framework is imported already.", async token =>
        {
            string? downloaded = null;
            if (downloads.Count > 0)
            {
                await RunDownloadAsync(downloads, token);
                lock (gate)
                    downloaded = job.Summary;
            }

            if (token.IsCancellationRequested)
                return;

            await RunImportAsync(currentPlan, mods, new HashSet<string>(StringComparer.OrdinalIgnoreCase), token);
            if (downloaded is not null)
            {
                lock (gate)
                    job.Summary = $"{downloaded.TrimEnd('.')}; {job.Summary}";
            }
        });
    }

    public Task CancelAsync(CancellationToken ct = default)
    {
        lock (gate)
        {
            if (job.Running && !job.Cancelling)
            {
                job.Cancelling = true;
                cancel?.Cancel();
                AddLog("Stopping after the current item…");
            }
        }

        return Task.CompletedTask;
    }

    public Task<ImportPlanJsonDto> GetPlanAsync(CancellationToken ct = default)
    {
        LoadPlan();
        lock (gate)
            return Task.FromResult(new ImportPlanJsonDto { Json = File.ReadAllText(PlanPath) });
    }

    public Task<ImportPlanInfoDto> ReplacePlanAsync(ImportPlanJsonDto dto, CancellationToken ct = default)
    {
        var parsed = ImportPlan.Parse(dto.Json); // throws with the problems
        lock (gate)
        {
            ThrowIfRunning();
            Directory.CreateDirectory(Root);
            File.WriteAllText(PlanPath, dto.Json);
            var current = State();
            current.PlanUploadedAt = DateTime.UtcNow;
            SaveState();
            (plan, planError, planLoaded) = (parsed, null, true);
            AddLog($"The import plan was replaced: {parsed.Mods.Count} mods.");
            return Task.FromResult(PlanInfo(plan, null));
        }
    }

    public Task<ImportPlanInfoDto> ResetPlanAsync(CancellationToken ct = default)
    {
        lock (gate)
        {
            ThrowIfRunning();
            Directory.CreateDirectory(Root);
            File.WriteAllText(PlanPath, BundledPlan());
            State().PlanUploadedAt = null;
            SaveState();
            planLoaded = false;
        }

        var (current, error) = LoadPlan();
        lock (gate)
        {
            AddLog("The import plan was reset to the one shipped with the server.");
            return Task.FromResult(PlanInfo(current, error));
        }
    }

    /// <summary>The names of the frameworks the plan can import (setup page); empty while the plan is unusable.</summary>
    public IReadOnlySet<string> FrameworkNamesInPlan() =>
        new HashSet<string>(LoadPlan().Plan?.Frameworks().Select(m => m.Name) ?? [], StringComparer.OrdinalIgnoreCase);

    // ── Steps ─────────────────────────────────────────────────────────────

    private ImportJobDto Start(ImportJobKind kind, int total, string nothingToDo, Func<CancellationToken, Task> run)
    {
        lock (gate)
        {
            ThrowIfRunning();
            var now = DateTime.UtcNow;
            if (total == 0)
            {
                job = new ImportJobDto { Kind = kind, StartedAt = now, FinishedAt = now, Summary = nothingToDo };
                return Copy(job);
            }

            job = new ImportJobDto { Kind = kind, Running = true, Total = total, StartedAt = now };
            cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            var token = cancel.Token;
            AddLog(kind switch
            {
                ImportJobKind.Download => $"Download started: {total} files.",
                ImportJobKind.Frameworks => $"Framework import started: {total} files and mods.",
                _ => $"Import started: {total} mods.",
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    await run(token);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "The import step {Kind} failed.", kind);
                    lock (gate)
                    {
                        AddLog($"The step stopped because of an error: {ex.Message}", isError: true);
                        job.Summary ??= $"Stopped because of an error: {ex.Message}";
                    }
                }
                finally
                {
                    lock (gate)
                    {
                        job.Running = false;
                        job.Current = null;
                        job.FinishedAt = DateTime.UtcNow;
                        SaveState();
                    }
                }
            }, CancellationToken.None);

            return Copy(job);
        }
    }

    private async Task RunDownloadAsync(List<DownloadItem> work, CancellationToken token)
    {
        var client = httpClients.CreateClient(HttpClientName);
        int fetched = 0, failed = 0;
        long bytes = 0;

        lock (gate)
        {
            foreach (var mod in work.Select(w => w.Mod).Distinct())
                ModState(mod.Key).DownloadIssue = null;
        }

        foreach (var item in work)
        {
            if (token.IsCancellationRequested)
                break;

            lock (gate)
                job.Current = $"{item.Mod.Name}: {Path.GetFileName(item.Target)}";

            try
            {
                bytes += await DownloadAsync(client, item, token);
                if (item.ArchiveRoot is { Length: > 0 } root && CheckArchiveRoot(item.Target, root) is { } problem)
                    throw new InvalidDataException(problem);
                fetched++;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException)
            {
                failed++;
                var message = $"{Path.GetFileName(item.Target)}: {ex.Message}";
                lock (gate)
                {
                    ModState(item.Mod.Key).DownloadIssue = message;
                    AddLog($"{item.Mod.Name}: download failed. {message}", isError: true);
                }
            }

            lock (gate)
                job.Done++;

            try
            {
                await Task.Delay(DownloadPause, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        lock (gate)
        {
            var summary = string.Create(CultureInfo.InvariantCulture,
                $"{fetched} file{(fetched == 1 ? "" : "s")} downloaded ({bytes / 1024.0 / 1024.0:0.#} MB), {failed} failed");
            job.Summary = token.IsCancellationRequested ? $"Cancelled: {summary}." : $"{summary}.";
            AddLog($"Download finished: {job.Summary}", isError: failed > 0);
        }
    }

    private async Task RunImportAsync(ImportPlan currentPlan, List<PlanMod> work, HashSet<string> accepted, CancellationToken token)
    {
        var byKey = currentPlan.Mods.ToDictionary(m => m.Key, StringComparer.OrdinalIgnoreCase);
        int stored = 0, heldBack = 0, failed = 0, notDownloaded = 0;

        foreach (var mod in work)
        {
            if (token.IsCancellationRequested)
                break;

            lock (gate)
            {
                job.Current = mod.Name;
                var modState = ModState(mod.Key);
                modState.ImportIssue = null;
                modState.HeldBackVersion = null;
                modState.Warnings = [];
            }

            try
            {
                switch (await ImportModAsync(mod, byKey, accepted.Contains(mod.Key), token))
                {
                    case ModOutcome.Stored: stored++; break;
                    case ModOutcome.HeldBack: heldBack++; break;
                    case ModOutcome.NotDownloaded: notDownloaded++; break;
                    case ModOutcome.Failed: failed++; break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // One broken mod must not stop the others.
                logger.LogError(ex, "Importing {Key} failed.", mod.Key);
                failed++;
                Fail(mod, $"Unexpected error: {ex.Message}");
            }

            lock (gate)
            {
                job.Done++;
                SaveState();
            }
        }

        lock (gate)
        {
            var summary = $"{stored} imported, {heldBack} held back for warnings, {failed} failed";
            if (notDownloaded > 0)
                summary += $", {notDownloaded} not downloaded yet";
            job.Summary = token.IsCancellationRequested ? $"Cancelled: {summary}." : $"{summary}.";
            AddLog($"Import finished: {job.Summary}", isError: failed > 0);
        }
    }

    private enum ModOutcome { Stored, HeldBack, NotDownloaded, Failed }

    /// <summary>Imports the versions of one mod that are not on the site yet, oldest first.</summary>
    private async Task<ModOutcome> ImportModAsync(PlanMod mod, Dictionary<string, PlanMod> byKey, bool acceptWarnings, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var importer = scope.ServiceProvider.GetRequiredService<ModImportService>();

        var existing = await importer.FindAsync(mod.Name, mod.Author, token);
        var have = new HashSet<string>(existing?.Versions ?? [], StringComparer.OrdinalIgnoreCase);
        var missing = mod.Versions.Where(v => !have.Contains(v.Version)).ToList();
        if (missing.Count == 0)
            return ModOutcome.Stored;

        if (missing.SelectMany(v => v.Files.Select(f => FilePath(mod, v, f))).Any(p => !IsDownloaded(p)))
        {
            lock (gate)
                AddLog($"{mod.Name}: skipped, its files are not all downloaded yet.");
            return ModOutcome.NotDownloaded;
        }

        var dependencyIds = new List<int>();
        foreach (var dependency in mod.Dependencies.Select(k => byKey[k]))
        {
            if (await importer.FindAsync(dependency.Name, dependency.Author, token) is not { } found)
                return Fail(mod, $"Needs {dependency.Name}, which is not imported yet.");
            dependencyIds.Add(found.GroupId);
        }

        var groupId = existing?.GroupId;
        var origin = new UploadOrigin(mod.F95Owner?.UserId, mod.F95Owner?.Name, mod.SourceUrl);
        foreach (var version in missing)
        {
            token.ThrowIfCancellationRequested();
            lock (gate)
                job.Current = $"{mod.Name} {version.Version}";

            var streams = new List<FileStream>();
            try
            {
                var files = version.Files.Select(f =>
                {
                    var stream = File.OpenRead(FilePath(mod, version, f));
                    streams.Add(stream);
                    return new UploadFile(AsFormFile(stream, "files", f.Name), f.Name, f.InstallPath ?? string.Empty, f.ArchiveRoot ?? string.Empty, f.Template);
                }).ToList();

                IFormFile? preview = null;
                if (groupId is null && mod.Preview is not null && IsDownloaded(PreviewPath(mod)))
                {
                    var stream = File.OpenRead(PreviewPath(mod));
                    streams.Add(stream);
                    preview = AsFormFile(stream, "previewImage", Path.GetFileName(PreviewPath(mod)));
                }

                var dto = new ModUploadDto
                {
                    Name = mod.Name,
                    Description = mod.Description,
                    Version = version.Version,
                    Category = mod.Category,
                    ModGroupId = groupId,
                    DependencyGroupIds = dependencyIds,
                    OptionalGroups = [.. mod.OptionalGroups.Select(g => new OptionalGroupDto { Name = g.Name, Patterns = [.. g.Patterns] })],
                    AcceptWarnings = acceptWarnings,
                };

                var result = await importer.ImportAsync(dto, files, preview, mod.Author, version.UploadedAt ?? mod.PostedAt, origin, token);
                if (result.Unconfirmed is { } warnings)
                {
                    lock (gate)
                    {
                        var modState = ModState(mod.Key);
                        modState.HeldBackVersion = version.Version;
                        modState.Warnings = [.. warnings];
                        AddLog($"{mod.Name} {version.Version}: held back for {warnings.Count} warning{(warnings.Count == 1 ? "" : "s")}.");
                    }
                    return ModOutcome.HeldBack;
                }

                if (result.Mod is not { } stored)
                    return Fail(mod, $"Version {version.Version}: {result.Error ?? (result.Forbidden ? "the mod belongs to someone else." : "rejected.")}");

                groupId = stored.ModGroupId;
                lock (gate)
                    AddLog($"{mod.Name} {version.Version}: imported{(acceptWarnings ? " (warnings accepted)" : "")}.");
            }
            finally
            {
                foreach (var stream in streams)
                    await stream.DisposeAsync();
            }
        }

        return ModOutcome.Stored;
    }

    private ModOutcome Fail(PlanMod mod, string issue)
    {
        lock (gate)
        {
            ModState(mod.Key).ImportIssue = issue;
            AddLog($"{mod.Name}: {issue}", isError: true);
        }
        return ModOutcome.Failed;
    }

    // ── Downloads ─────────────────────────────────────────────────────────

    /// <summary>The files and previews of these mods that are not on the server yet.</summary>
    private static List<DownloadItem> DownloadWork(IEnumerable<PlanMod> mods)
    {
        var work = new List<DownloadItem>();
        foreach (var mod in mods)
        {
            foreach (var version in mod.Versions)
            {
                foreach (var file in version.Files.Where(f => !IsDownloaded(FilePath(mod, version, f))))
                    work.Add(new DownloadItem(mod, file.Url, FilePath(mod, version, file), file.Member, file.ArchiveRoot));
            }

            if (mod.Preview is { } preview && !IsDownloaded(PreviewPath(mod)))
                work.Add(new DownloadItem(mod, preview, PreviewPath(mod), null, null));
        }

        return work;
    }

    private sealed record DownloadItem(PlanMod Mod, string Url, string Target, string? Member, string? ArchiveRoot);

    /// <summary>Downloads one file to its place (through a .part file). Returns the bytes received.</summary>
    private static async Task<long> DownloadAsync(HttpClient client, DownloadItem item, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(item.Target)!);
        var part = item.Target + ".part";
        long received = 0;
        try
        {
            using (var response = await client.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, token))
            {
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"the server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
                if (response.Content.Headers.ContentType?.MediaType is "text/html")
                    throw new InvalidDataException("got a web page instead of the file (removed, or F95zone wants a sign-in).");
                if (response.Content.Headers.ContentLength > Configuration.MaxModSize)
                    throw new InvalidDataException("the file is larger than 512 MB.");

                await using var source = await response.Content.ReadAsStreamAsync(token);
                await using var target = File.Create(part);
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    received += read;
                    if (received > Configuration.MaxModSize)
                        throw new InvalidDataException("the file is larger than 512 MB.");
                    await target.WriteAsync(buffer.AsMemory(0, read), token);
                }
            }

            if (item.Member is { Length: > 0 } member)
                ExtractMember(part, member, item.Target);
            else
                File.Move(part, item.Target, overwrite: true);

            return received;
        }
        finally
        {
            File.Delete(part);
        }
    }

    /// <summary>Keeps one file of a downloaded .zip as <paramref name="target"/>; the archive itself is dropped.</summary>
    private static void ExtractMember(string zipPath, string member, string target)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var wanted = member.Replace('\\', '/');
        var entry = archive.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"the archive has no '{member}'.");
        if (entry.Length > Configuration.MaxModSize)
            throw new InvalidDataException($"'{member}' is larger than 512 MB.");

        var temp = target + ".member";
        try
        {
            using (var source = entry.Open())
            using (var output = File.Create(temp))
                source.CopyTo(output);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>Null when the archive has files inside <paramref name="root"/>; otherwise what is wrong.</summary>
    private static string? CheckArchiveRoot(string path, string root)
    {
        using var stream = File.OpenRead(path);
        var entries = ModFileInspector.ListEntries(stream);
        if (entries is null)
            return $"{Path.GetFileName(path)} cannot be read as an archive.";

        var prefix = root.Replace('\\', '/').Trim('/') + "/";
        return entries.Any(e => e.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ? null
            : $"{Path.GetFileName(path)} has nothing inside the folder '{root}' the plan names.";
    }

    private static FormFile AsFormFile(Stream stream, string field, string fileName) =>
        new(stream, 0, stream.Length, field, fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/octet-stream",
        };

    // ── Status ────────────────────────────────────────────────────────────

    /// <summary>Imported versions per mod, by name and author credit, as the import endpoint matches them.</summary>
    private async Task<Dictionary<(string Name, string Author), List<(string Version, int Id)>>> ImportedVersionsAsync(ImportPlan currentPlan, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var names = currentPlan.Mods.Select(m => m.Name).Distinct().ToList();
        var rows = await db.Mods.AsNoTracking()
            .Where(m => names.Contains(m.Name))
            .OrderBy(m => m.UploadedAt)
            .Select(m => new { m.Name, m.Author, m.Version, m.Id })
            .ToListAsync(ct);

        return rows.GroupBy(r => (r.Name, r.Author))
            .ToDictionary(g => g.Key, g => g.Select(r => (r.Version, r.Id)).ToList());
    }

    private static bool IsImported(Dictionary<(string Name, string Author), List<(string Version, int Id)>> imported, PlanMod mod, string version) =>
        imported.TryGetValue((mod.Name, mod.Author), out var versions) && versions.Any(v => v.Version.Equals(version, StringComparison.OrdinalIgnoreCase));

    /// <summary>Call while holding the gate.</summary>
    private ImportModDto Describe(PlanMod mod, Dictionary<(string Name, string Author), List<(string Version, int Id)>> imported)
    {
        var files = mod.Versions.SelectMany(v => v.Files.Select(f => FilePath(mod, v, f))).ToList();
        var downloaded = files.Count(IsDownloaded);
        var versionsImported = mod.Versions.Count(v => IsImported(imported, mod, v.Version));
        imported.TryGetValue((mod.Name, mod.Author), out var rows);
        var modState = State().Mods.GetValueOrDefault(mod.Key) ?? new ModStateRecord();

        var state =
            modState.HeldBackVersion is not null ? ImportModState.HeldBack
            : modState.ImportIssue is not null ? ImportModState.Failed
            : versionsImported == mod.Versions.Count ? ImportModState.Imported
            : versionsImported > 0 ? ImportModState.PartlyImported
            : modState.DownloadIssue is not null ? ImportModState.DownloadFailed
            : downloaded < files.Count ? ImportModState.NotDownloaded
            : ImportModState.Downloaded;

        return new ImportModDto
        {
            Key = mod.Key,
            Name = mod.Name,
            Author = mod.Author,
            Category = mod.Category,
            SourceUrl = mod.SourceUrl,
            State = state,
            ModId = rows?.LastOrDefault().Id is > 0 and var id ? id : null,
            Versions = mod.Versions.Count,
            VersionsImported = versionsImported,
            Files = files.Count,
            FilesDownloaded = downloaded,
            Issue = state == ImportModState.Failed ? modState.ImportIssue : modState.DownloadIssue,
            HeldBackVersion = modState.HeldBackVersion,
            Warnings = [.. modState.Warnings],
            Dependencies = [.. mod.Dependencies],
            Note = mod.Note,
        };
    }

    /// <summary>Call while holding the gate.</summary>
    private ImportPlanInfoDto PlanInfo(ImportPlan? currentPlan, string? error)
    {
        var info = new ImportPlanInfoDto
        {
            IsBundled = State().PlanUploadedAt is null,
            UploadedAt = State().PlanUploadedAt,
            Error = error,
        };
        if (currentPlan is null)
            return info;

        info.Mods = currentPlan.Mods.Count;
        info.Versions = currentPlan.Mods.Sum(m => m.Versions.Count);
        foreach (var mod in currentPlan.Mods)
        {
            foreach (var version in mod.Versions)
            {
                foreach (var file in version.Files)
                {
                    info.Files++;
                    var path = FilePath(mod, version, file);
                    if (IsDownloaded(path))
                    {
                        info.FilesDownloaded++;
                        info.DownloadedBytes += new FileInfo(path).Length;
                    }
                }
            }
        }

        return info;
    }

    private static ImportJobDto Copy(ImportJobDto source) => new()
    {
        Kind = source.Kind,
        Running = source.Running,
        Cancelling = source.Cancelling,
        Done = source.Done,
        Total = source.Total,
        Current = source.Current,
        StartedAt = source.StartedAt,
        FinishedAt = source.FinishedAt,
        Summary = source.Summary,
    };

    // ── Plan and state ────────────────────────────────────────────────────

    private ImportPlan RequirePlan()
    {
        var (currentPlan, error) = LoadPlan();
        return currentPlan ?? throw new InvalidOperationException(error ?? "There is no import plan.");
    }

    /// <summary>
    /// The plan in use. The first time, the shipped plan is copied to storage; it is copied again when a newer server
    /// ships a different one, unless an admin uploaded their own.
    /// </summary>
    private (ImportPlan? Plan, string? Error) LoadPlan()
    {
        lock (gate)
        {
            if (planLoaded)
                return (plan, planError);

            Directory.CreateDirectory(Root);
            var bundled = BundledPlan();
            if (!File.Exists(PlanPath) || (State().PlanUploadedAt is null && File.ReadAllText(PlanPath) != bundled))
                File.WriteAllText(PlanPath, bundled);

            try
            {
                (plan, planError) = (ImportPlan.Parse(File.ReadAllText(PlanPath)), null);
            }
            catch (InvalidOperationException ex)
            {
                (plan, planError) = (null, ex.Message);
            }

            planLoaded = true;
            return (plan, planError);
        }
    }

    private static string BundledPlan()
    {
        using var stream = typeof(F95ImportService).Assembly.GetManifestResourceStream(BundledPlanResource)
                           ?? throw new InvalidOperationException("The server was built without its import plan.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void ThrowIfRunning()
    {
        if (job.Running)
            throw new InvalidOperationException("Another step is running. Wait for it to finish, or cancel it.");
    }

    /// <summary>Call while holding the gate.</summary>
    private ImportState State()
    {
        if (state is not null)
            return state;

        try
        {
            state = File.Exists(StatePath) ? JsonSerializer.Deserialize<ImportState>(File.ReadAllText(StatePath)) : null;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Ignoring an unreadable {Path}.", StatePath);
        }

        state ??= new ImportState();
        state.Mods = new Dictionary<string, ModStateRecord>(state.Mods, StringComparer.OrdinalIgnoreCase);
        return state;
    }

    /// <summary>Call while holding the gate.</summary>
    private ModStateRecord ModState(string key)
    {
        var mods = State().Mods;
        if (!mods.TryGetValue(key, out var record))
            mods[key] = record = new ModStateRecord();
        return record;
    }

    /// <summary>Call while holding the gate.</summary>
    private void SaveState()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(StatePath, JsonSerializer.Serialize(State(), StateJson));
    }

    /// <summary>Call while holding the gate.</summary>
    private void AddLog(string message, bool isError = false)
    {
        log.Add(new ImportLogLineDto { At = DateTime.UtcNow, IsError = isError, Message = message });
        if (log.Count > MaxLogLines)
            log.RemoveRange(0, log.Count - MaxLogLines);
        if (isError)
            logger.LogWarning("F95 import: {Message}", message);
        else
            logger.LogInformation("F95 import: {Message}", message);
    }

    private static string FilePath(PlanMod mod, PlanVersion version, PlanFile file) =>
        Path.Combine(FilesRoot, mod.Key, version.Version, file.Name);

    private static string PreviewPath(PlanMod mod) =>
        Path.Combine(FilesRoot, mod.Key, "preview" + ImportPlan.PreviewExtension(mod.Preview!));

    private static bool IsDownloaded(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length > 0;
    }

    /// <summary>Kept in Storage/Import/state.json across restarts.</summary>
    private sealed class ImportState
    {
        /// <summary>Set while an uploaded plan is in use; null for the shipped one.</summary>
        public DateTime? PlanUploadedAt { get; set; }

        public Dictionary<string, ModStateRecord> Mods { get; set; } = [];
    }

    /// <summary>What went wrong last with one mod.</summary>
    private sealed class ModStateRecord
    {
        public string? DownloadIssue { get; set; }
        public string? ImportIssue { get; set; }
        public string? HeldBackVersion { get; set; }
        public List<string> Warnings { get; set; } = [];
    }
}
