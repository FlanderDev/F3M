using System.Text;
using System.Text.Json;
using F3M.Desktop.Core;
using F3M.Shared.Helpers;
using F3M.Shared.Models;

namespace F3M.Desktop.Services;

/// <summary>One file a deployed version places. Copy is false when a config file is kept because the user changed it.</summary>
public sealed record PlannedFile(int GroupId, DeployedFile Record, string? Source, bool Copy);

/// <summary>
/// What a deploy will do, computed before anything is written. Nothing in the game folder changes until
/// <see cref="Deploy.Apply"/> is called with this plan.
/// </summary>
public sealed class DeployPlan
{
    public string ProfileId { get; init; } = string.Empty;
    public string ProfileName { get; init; } = string.Empty;
    public List<DeployedMod> Stays { get; } = [];
    public List<DeployedMod> Removed { get; } = [];
    public List<VersionDocument> Added { get; } = [];
    public List<PlannedFile> Files { get; } = [];

    /// <summary>Absolute paths of unchanged files of removed mods.</summary>
    public List<string> Deletes { get; } = [];

    /// <summary>Generated files of removed mods that are disposable. Removed without asking.</summary>
    public List<string> CacheRemovals { get; } = [];

    /// <summary>Generated files of removed mods. Removed only after confirmation.</summary>
    public List<string> GeneratedRemovals { get; } = [];

    /// <summary>Game-relative paths that replace a file F3M did not place, or a plugin the user changed. Needs confirmation.</summary>
    public List<string> Overwrites { get; } = [];

    /// <summary>Messages about files that are kept as they are.</summary>
    public List<string> Kept { get; } = [];

    /// <summary>A reason the deploy cannot run at all.</summary>
    public List<string> Blocked { get; } = [];

    public List<VersionDocument> NeedsDownload { get; } = [];
    public long DownloadBytes { get; set; }

    /// <summary>Write time of the deployed-state file when the plan was made. Apply refuses a plan that is out of date.</summary>
    public DateTime? StateStamp { get; set; }

    public bool HasChanges => Added.Count > 0 || Removed.Count > 0;
    public bool NeedsConfirmation => Overwrites.Count > 0 || GeneratedRemovals.Count > 0;

    public string Summary
    {
        get
        {
            var updated = Added.Count(d => Removed.Any(r => r.GroupId == d.GroupId));
            var added = Added.Count - updated;
            var removed = Removed.Count(r => Added.All(d => d.GroupId != r.GroupId));
            var files = Files.Count(f => f.Copy) + Deletes.Count + CacheRemovals.Count + GeneratedRemovals.Count;
            if (!HasChanges && files == 0) return "No changes";
            return $"+{added} mods, −{removed} mods, {updated} updates, {files} files";
        }
    }

    /// <summary>Text for the confirmation dialog, in <see cref="Markup"/>: listed files are clickable.</summary>
    public string Describe(string gameRoot)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Summary);
        if (DownloadBytes > 0) sb.AppendLine($"About {FileOps.FormatBytes(DownloadBytes)} to download first.");
        AppendList(sb, "Generated files that will be removed:", GeneratedRemovals.Select(p => Markup.Link(p, FileOps.ToRelative(gameRoot, p))));
        AppendList(sb, "Files F3M did not place, which will be replaced:",
            Overwrites.Select(to => Markup.Link(Path.Combine(gameRoot, to.Replace('/', Path.DirectorySeparatorChar)), to)));
        AppendList(sb, "Kept as they are:", Kept.Select(Markup.Escape));
        return sb.ToString();
    }

    private static void AppendList(StringBuilder sb, string heading, IEnumerable<string> items)
    {
        var list = items.Take(25).ToList();
        if (list.Count == 0) return;
        sb.AppendLine().AppendLine(heading);
        foreach (var item in list) sb.AppendLine($"  {item}");
        var total = items.Count();
        if (total > list.Count) sb.AppendLine($"  and {total - list.Count} more");
    }
}

/// <summary>
/// Makes the game folder match a profile (plan 6.4). Every change is written to a journal first, with a backup of
/// anything it replaces, so an interrupted deploy can be rolled back at the next start.
/// </summary>
public sealed class Deploy(AppServices app)
{
    private const string TempSuffix = ".f3m-tmp";

    private string StateFile => Path.Combine(app.Paths.State, app.GameId + ".json");
    private string JournalFile => Path.Combine(app.Paths.Journal, "current.ndjson");

    /// <summary>An interrupted or failed deploy whose rollback has not finished yet.</summary>
    public bool HasPendingRollback => File.Exists(JournalFile);

    public DeployedState LoadState()
    {
        try
        {
            return FileOps.ReadJson<DeployedState>(StateFile) ?? new DeployedState();
        }
        catch (JsonException ex)
        {
            AppLog.Error("The deployed-state file is unreadable", ex);
            throw new UserException("The record of deployed files is damaged. See the diagnostics log in Settings.");
        }
    }

    private DateTime? StateStampNow() => File.Exists(StateFile) ? File.GetLastWriteTimeUtc(StateFile) : null;

    /// <summary>Absolute paths of generated files a deployed mod's patterns match now, for "Clear generated files".</summary>
    public List<string> GeneratedFilesFor(DeployedMod mod)
    {
        var root = app.Game.RootOrThrow();
        var owned = mod.Files.Select(f => f.To).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var generated in mod.GeneratedPaths)
        {
            if (PlacementRules.GeneratedKindOf(generated.Kind) == GeneratedPathKind.UserData) continue;
            foreach (var relative in FindGenerated(root, generated.Pattern, mod.DeployedAt))
            {
                if (owned.Contains(relative)) continue;
                result.Add(FileOps.ResolveUnder(root, relative));
            }
        }

        return result;
    }

    /// <summary>Resolves the profile against the catalog and compares it with what is deployed. Reads files; writes nothing.</summary>
    public async Task<DeployPlan> PlanAsync(ProfileDef profile, CancellationToken ct)
    {
        var target = await app.Catalog.ResolveAsync(profile.GroupIds, profile.Pins, ct);
        return PlanCore(profile, target);
    }

    private DeployPlan PlanCore(ProfileDef profile, Dictionary<int, VersionDocument> target)
    {
        var root = app.Game.RootOrThrow();
        var state = LoadState();
        var plan = new DeployPlan { ProfileId = profile.Id, ProfileName = profile.Name, StateStamp = StateStampNow() };

        foreach (var mod in state.Mods)
        {
            if (target.TryGetValue(mod.GroupId, out var doc) && doc.VersionId == mod.VersionId)
                plan.Stays.Add(mod);
            else
                plan.Removed.Add(mod);
        }

        // Which group writes each game-relative path in the target profile. Two groups writing one path blocks the deploy.
        var writers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (groupId, doc) in target)
        {
            foreach (var file in doc.Files)
            foreach (var placement in file.Placements)
            {
                var to = PlacementRules.Normalize(placement.To, "game folder");
                if (writers.TryGetValue(to, out var other) && other != groupId)
                    plan.Blocked.Add($"{doc.Name} and {target[other].Name} both write {to}.");
                else
                    writers[to] = groupId;
            }
        }

        if (plan.Blocked.Count > 0) return plan;

        // The previous record of every deployed path, so a changed file can be compared with what F3M wrote.
        var previous = new Dictionary<string, DeployedFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in state.Mods)
            foreach (var file in mod.Files)
                previous.TryAdd(file.To, file);

        var keptPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in plan.Stays)
            foreach (var file in mod.Files) keptPaths.Add(file.To);
        foreach (var path in writers.Keys) keptPaths.Add(path);

        foreach (var mod in plan.Removed)
        {
            foreach (var file in mod.Files)
            {
                if (writers.ContainsKey(file.To)) continue;
                var path = FileOps.ResolveUnder(root, file.To);
                if (!File.Exists(path)) continue;
                if (FileOps.Sha256File(path) == file.Sha256)
                    plan.Deletes.Add(path);
                else
                    plan.Kept.Add($"Keeps your changes to {Path.GetFileName(file.To)}");
            }
        }

        var planned = new HashSet<string>(plan.Deletes, StringComparer.OrdinalIgnoreCase);
        foreach (var mod in plan.Removed)
        {
            foreach (var generated in mod.GeneratedPaths)
            {
                var kind = PlacementRules.GeneratedKindOf(generated.Kind);
                if (kind == GeneratedPathKind.UserData) continue;

                foreach (var relative in FindGenerated(root, generated.Pattern, mod.DeployedAt))
                {
                    if (keptPaths.Contains(relative)) continue;
                    var path = FileOps.ResolveUnder(root, relative);
                    if (!planned.Add(path)) continue;
                    if (kind == GeneratedPathKind.Cache) plan.CacheRemovals.Add(path);
                    else plan.GeneratedRemovals.Add(path);
                }
            }
        }

        foreach (var (groupId, doc) in target)
        {
            if (plan.Stays.Any(s => s.GroupId == groupId)) continue;
            plan.Added.Add(doc);
            if (!app.Downloads.IsCached(groupId, doc.VersionId)) plan.NeedsDownload.Add(doc);

            foreach (var file in doc.Files)
            foreach (var placement in file.Placements)
            {
                var to = PlacementRules.Normalize(placement.To, "game folder");
                var kind = PlacementRules.KindOf(placement.Kind);
                var path = FileOps.ResolveUnder(root, to);
                if (FileOps.HasReparsePointBelow(root, path))
                {
                    plan.Blocked.Add($"{to} passes through a link or junction in the game folder.");
                    continue;
                }

                if (Directory.Exists(path))
                {
                    plan.Blocked.Add($"{to} is a folder in the game folder.");
                    continue;
                }

                previous.TryGetValue(to, out var prev);
                var exists = File.Exists(path);
                var currentHash = exists ? FileOps.Sha256File(path) : null;
                var unchangedByUser = prev is not null && currentHash == prev.Sha256;

                if (exists && kind == PlacementKind.Config && !unchangedByUser)
                {
                    // A config the user has changed, or one F3M did not write. Keep it and record the current state.
                    plan.Kept.Add($"Keeps your changes to {Path.GetFileName(to)}");
                    plan.Files.Add(new PlannedFile(groupId, new DeployedFile
                    {
                        To = to,
                        Sha256 = currentHash!,
                        Size = new FileInfo(path).Length,
                        Kind = kind.ToString().ToLowerInvariant(),
                        CreatedByMod = prev?.CreatedByMod ?? false,
                        DefaultSha256 = placement.Sha256,
                    }, null, Copy: false));
                    continue;
                }

                if (exists && !unchangedByUser)
                    plan.Overwrites.Add(to);

                plan.Files.Add(new PlannedFile(groupId, new DeployedFile
                {
                    To = to,
                    Sha256 = placement.Sha256,
                    Size = placement.Size,
                    Kind = kind.ToString().ToLowerInvariant(),
                    CreatedByMod = !exists || (prev?.CreatedByMod ?? false),
                    DefaultSha256 = placement.Sha256,
                }, app.Downloads.PayloadPath(doc, file.FileId, placement.From), Copy: true));
            }
        }

        plan.DownloadBytes = app.Downloads.BytesToDownload(plan.NeedsDownload);
        return plan;
    }

    /// <summary>
    /// Runs the plan. Requires the cache to be complete and, when the plan needs confirmation, <paramref name="confirmed"/>.
    /// On any failure the journal is rolled back before the exception leaves this method.
    /// </summary>
    public void Apply(DeployPlan plan, bool confirmed, CancellationToken ct)
    {
        if (plan.Blocked.Count > 0) throw new UserException(plan.Blocked[0]);
        if (StateStampNow() != plan.StateStamp)
            throw new UserException("The deployed files changed while the deploy was being prepared. Review it again.");
        if (plan.NeedsDownload.Count > 0) throw new UserException("Some mods are not downloaded yet. Download them first.");
        if (plan.NeedsConfirmation && !confirmed) throw new UserException("The deploy needs your confirmation.");
        if (app.Game.IsRunning()) throw new UserException("The game is running. Close it before deploying.");
        foreach (var file in plan.Files.Where(f => f.Copy))
        {
            if (!File.Exists(file.Source))
                throw new UserException($"The cached copy of {file.Record.To} is missing. Download the mod again.");
        }

        // An earlier deploy that could not be fully undone must be undone first: a new journal would replace its record.
        if (!Rollback())
            throw new UserException("An earlier deploy could not be fully undone, because a file in the game folder is in use. " +
                                    "Close the game and anything else using the game folder, then try again.");

        var root = app.Game.RootOrThrow();
        var journal = new DeployJournal(JournalFile, Path.Combine(app.Paths.Journal, Guid.NewGuid().ToString("N")));
        var createdDirs = new Dictionary<int, List<string>>();
        try
        {
            foreach (var path in plan.Deletes.Concat(plan.CacheRemovals).Concat(plan.GeneratedRemovals))
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(path)) continue;
                journal.Backup(path, "delete");
                File.Delete(path);
            }

            foreach (var file in plan.Files.Where(f => f.Copy))
            {
                ct.ThrowIfCancellationRequested();
                var path = FileOps.ResolveUnder(root, file.Record.To);
                var dir = Path.GetDirectoryName(path)!;
                if (!createdDirs.ContainsKey(file.GroupId)) createdDirs[file.GroupId] = [];
                createdDirs[file.GroupId].AddRange(EnsureDirectory(journal, dir));

                if (File.Exists(path))
                    journal.Backup(path, "copy");
                else
                    journal.Created(path, "copy");

                var temp = path + TempSuffix;
                File.Copy(file.Source!, temp, overwrite: true);
                File.Move(temp, path, overwrite: true);
            }

            foreach (var mod in plan.Removed)
            foreach (var dir in mod.CreatedDirectories.OrderByDescending(d => d.Length))
                TryRemoveEmpty(dir);

            var now = DateTimeOffset.UtcNow;
            var mods = new List<DeployedMod>(plan.Stays);
            foreach (var doc in plan.Added)
            {
                mods.Add(new DeployedMod
                {
                    GroupId = doc.GroupId,
                    VersionId = doc.VersionId,
                    Name = doc.Name,
                    Version = doc.Version,
                    DeployedAt = now,
                    Files = plan.Files.Where(f => f.GroupId == doc.GroupId).Select(f => f.Record).ToList(),
                    GeneratedPaths = doc.GeneratedPaths.ToList(),
                    CreatedDirectories = createdDirs.TryGetValue(doc.GroupId, out var dirs) ? dirs : new List<string>(),
                });
            }

            FileOps.WriteJsonAtomic(StateFile, new DeployedState
            {
                GamePath = root,
                ProfileId = plan.ProfileId,
                ProfileName = plan.ProfileName,
                Mods = mods,
            });

            journal.Finish();
            AppLog.Info($"Deployed {plan.ProfileName}: {plan.Summary}");
        }
        catch (OperationCanceledException)
        {
            if (!Rollback()) throw new UserException(IncompleteRollback);
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("Deploy failed, rolling back", ex);
            if (!Rollback()) throw new UserException($"The deploy failed: {ex.Message} {IncompleteRollback}");
            throw new UserException($"The deploy failed and was rolled back. {ex.Message}");
        }
    }

    private const string IncompleteRollback =
        "Some files could not be put back yet, because they are in use. Their backups are kept: close the game and " +
        "press Deploy, or restart F3M Desktop, to finish undoing it.";

    /// <summary>
    /// Restores the game folder from the journal, newest change first. Called on failure, at startup and before a
    /// deploy. Every step can be repeated safely, so when one fails the journal and its backups are kept and the whole
    /// rollback runs again next time. Returns false in that case, true when nothing is left to undo.
    /// </summary>
    public bool Rollback()
    {
        if (!File.Exists(JournalFile)) return true;

        var failed = 0;
        var lines = File.ReadAllLines(JournalFile).Where(l => l.Length > 0).ToList();
        var header = lines.Count > 0 ? JsonSerializer.Deserialize<JournalHeader>(lines[0], FileOps.JsonCompact) : null;
        foreach (var line in lines.Skip(1).Reverse())
        {
            try
            {
                var op = JsonSerializer.Deserialize<JournalOp>(line, FileOps.JsonCompact);
                if (op is null) continue;
                if (op.Kind == "mkdir")
                {
                    if (Directory.Exists(op.Path) && !Directory.EnumerateFileSystemEntries(op.Path).Any())
                        Directory.Delete(op.Path);
                }
                else if (op.Existed && op.Backup is not null)
                {
                    File.Copy(op.Backup, op.Path, overwrite: true);
                }
                else if (File.Exists(op.Path))
                {
                    File.Delete(op.Path);
                }

                if (op.Kind == "copy" && File.Exists(op.Path + TempSuffix)) File.Delete(op.Path + TempSuffix);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                AppLog.Error("A rollback step failed", ex);
                failed++;
            }
        }

        if (failed > 0)
        {
            AppLog.Error($"Rollback incomplete: {failed} step(s) failed. The journal and backups are kept for the next attempt.");
            return false;
        }

        if (header is not null)
        {
            var backups = Path.Combine(app.Paths.Journal, header.Id);
            if (Directory.Exists(backups)) Directory.Delete(backups, recursive: true);
        }

        File.Delete(JournalFile);
        AppLog.Info("Recovered an interrupted deploy: the game folder was rolled back");
        return true;
    }

    /// <summary>
    /// Files under the game folder that match a generated pattern and were written after the mod was deployed (plan 5.8).
    /// The timestamp rule is a heuristic: a pre-existing file the mod rewrites counts as generated.
    /// </summary>
    private static IEnumerable<string> FindGenerated(string root, string pattern, DateTimeOffset since)
    {
        var sinceUtc = since.UtcDateTime;
        if (!GeneratedPathRules.TryNormalize(pattern, out var normalized, out _)) yield break;

        var segments = normalized.Split('/');
        var literal = segments.TakeWhile(s => !s.Contains('*') && !s.Contains('?')).ToArray();
        if (literal.Length == segments.Length)
        {
            var exact = Path.Combine(root, string.Join(Path.DirectorySeparatorChar, literal));
            if (File.Exists(exact) && File.GetLastWriteTimeUtc(exact) > sinceUtc)
                yield return string.Join('/', literal);
            yield break;
        }

        var baseDir = literal.Length == 0 ? root : Path.Combine(root, string.Join(Path.DirectorySeparatorChar, literal));
        if (!Directory.Exists(baseDir)) yield break;
        foreach (var file in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
        {
            var relative = FileOps.ToRelative(root, file);
            if (GeneratedPathRules.IsMatch(normalized, relative) && File.GetLastWriteTimeUtc(file) > sinceUtc)
                yield return relative;
        }
    }

    /// <summary>Creates missing folders from the top down, journalling each one. Returns the folders it created.</summary>
    private static List<string> EnsureDirectory(DeployJournal journal, string dir)
    {
        var missing = new List<string>();
        for (var current = dir; !string.IsNullOrEmpty(current) && !Directory.Exists(current); current = Path.GetDirectoryName(current))
            missing.Add(current);

        missing.Reverse();
        foreach (var folder in missing)
        {
            journal.Created(folder, "mkdir");
            Directory.CreateDirectory(folder);
        }

        return missing;
    }

    private static void TryRemoveEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (IOException)
        {
            // Not empty or in use: leave it.
        }
    }

    /// <summary>Append-only journal: a header line, then one line per change, written before the change is made.</summary>
    private sealed class DeployJournal
    {
        private readonly string _file;
        private readonly string _backupDir;
        private int _count;

        public DeployJournal(string file, string backupDir)
        {
            _file = file;
            _backupDir = backupDir;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var header = new JournalHeader { Id = Path.GetFileName(backupDir), StartedAt = DateTimeOffset.UtcNow };
            File.WriteAllText(file, JsonSerializer.Serialize(header, FileOps.JsonCompact) + Environment.NewLine);
        }

        public void Backup(string path, string kind)
        {
            Directory.CreateDirectory(_backupDir);
            var backup = Path.Combine(_backupDir, $"{++_count}.bak");
            File.Copy(path, backup, overwrite: true);
            Record(new JournalOp { Kind = kind, Path = path, Existed = true, Backup = backup });
        }

        public void Created(string path, string kind) => Record(new JournalOp { Kind = kind, Path = path, Existed = false });

        public void Finish()
        {
            if (Directory.Exists(_backupDir)) Directory.Delete(_backupDir, recursive: true);
            if (File.Exists(_file)) File.Delete(_file);
        }

        private void Record(JournalOp op) =>
            File.AppendAllText(_file, JsonSerializer.Serialize(op, FileOps.JsonCompact) + Environment.NewLine);
    }
}
