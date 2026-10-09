using F3M.Desktop.Core;

namespace F3M.Desktop.Services;

/// <summary>Mod counts for the reset dialog. Deployed mods are in the game folder, which a reset does not touch.</summary>
public sealed record ResetModStats(int Profiles, int ProfileMods, int PinnedMods, int CachedMods, int CachedVersions, int DeployedMods);

/// <summary>One thing a factory reset removes, with its size. <see cref="IsDownload"/> marks downloaded mods.</summary>
public sealed record ResetItem(string Label, string Location, int Files, long Bytes, bool IsDownload, bool IsRegistration = false);

/// <summary>
/// Removes everything the app created: its data folder and its link and start-at-login registrations. Downloaded mods
/// only when asked. The game folder is never touched, so deployed mods stay where they are.
/// </summary>
public sealed class FactoryReset(AppServices app)
{
    /// <summary>Why a reset must not start now, or null. An interrupted deploy still needs its journal and backups.</summary>
    public string? BlockedReason()
    {
        if (app.Ops.Items.Any(i => !i.IsFinished))
            return "Wait until the running downloads and deploys finish, or cancel them, before a factory reset.";
        if (File.Exists(Path.Combine(app.Paths.Journal, "current.ndjson")))
            return "A deploy was interrupted and its backups are still needed. Deploy again, then reset.";
        return null;
    }

    /// <summary>Counts mods in profiles (all games), in the cache, and deployed in the current game folder.</summary>
    public ResetModStats ModStatistics()
    {
        var profiles = new List<ProfileDef>();
        if (Directory.Exists(app.Paths.Profiles))
        {
            foreach (var file in Directory.EnumerateFiles(app.Paths.Profiles, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    if (FileOps.ReadJson<ProfileDef>(file) is { } profile) profiles.Add(profile);
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
                {
                    // An unreadable profile is still removed; it just is not counted.
                }
            }
        }

        // A cached version is complete when its version.json exists: cache/{group}/{version}/version.json.
        var cachedVersions = Directory.Exists(app.Paths.Cache)
            ? Directory.EnumerateFiles(app.Paths.Cache, "version.json", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(app.Paths.Cache, f).Split(Path.DirectorySeparatorChar))
                .Where(parts => parts.Length == 3 && int.TryParse(parts[0], out _))
                .ToList()
            : [];

        int deployed;
        try
        {
            deployed = app.Game.IsConfigured ? app.Deploy.LoadState().Mods.Count : 0;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            deployed = 0;
        }

        return new ResetModStats(
            profiles.Count,
            profiles.SelectMany(p => p.GroupIds).Distinct().Count(),
            profiles.Sum(p => p.Pins.Count),
            cachedVersions.Select(parts => parts[0]).Distinct().Count(),
            cachedVersions.Count,
            deployed);
    }

    /// <summary>What a reset would remove right now. Folders and files that do not exist are left out.</summary>
    public List<ResetItem> Survey()
    {
        var paths = app.Paths;
        var items = new List<ResetItem>();

        void Folder(string label, string path, bool isDownload = false)
        {
            if (!Directory.Exists(path)) return;
            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToList();
            items.Add(new ResetItem(label, path, files.Count, files.Sum(SizeOf), isDownload));
        }

        Folder("Profiles", paths.Profiles);
        Folder("Deploy records", paths.State);
        Folder("Catalog copy", paths.Catalog);
        Folder("Config backups", paths.ConfigBackups);
        Folder("Logs", paths.Logs);
        Folder("Deploy journal", paths.Journal);
        Folder("Downloaded mods", paths.Cache, isDownload: true);
        Folder("Unfinished downloads", paths.Downloads, isDownload: true);

        // settings.json and anything else directly in the data folder. The single-instance socket stays, it is in use.
        var known = new[] { paths.Profiles, paths.State, paths.Catalog, paths.ConfigBackups, paths.Logs, paths.Journal, paths.Cache, paths.Downloads }
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var looseFiles = Directory.EnumerateFiles(paths.Root).Where(f => Path.GetFileName(f) != "f3m.sock").ToList();
        var otherFolders = Directory.EnumerateDirectories(paths.Root).Where(d => !known.Contains(Path.GetFullPath(d))).ToList();
        var otherFiles = looseFiles.Concat(otherFolders.SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))).ToList();
        if (otherFiles.Count > 0 || otherFolders.Count > 0)
            items.Add(new ResetItem("Settings and other files", paths.Root, otherFiles.Count, otherFiles.Sum(SizeOf), false));

        if (ProtocolRegistration.IsRegistered())
            items.Add(new ResetItem("f3m:// link registration", "for this user", 0, 0, false, IsRegistration: true));
        if (StartAtLogin.IsEnabled())
            items.Add(new ResetItem("Start at login", "for this user", 0, 0, false, IsRegistration: true));

        return items;
    }

    /// <summary>Removes the data folder's contents (downloads only with <paramref name="includeDownloads"/>) and the registrations.</summary>
    /// <returns>Paths that could not be removed, for example because another program holds them open.</returns>
    public List<string> Run(bool includeDownloads)
    {
        var paths = app.Paths;
        var failed = new List<string>();
        var keep = includeDownloads
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>([Path.GetFullPath(paths.Cache), Path.GetFullPath(paths.Downloads)], StringComparer.OrdinalIgnoreCase);

        foreach (var dir in Directory.EnumerateDirectories(paths.Root))
        {
            if (keep.Contains(Path.GetFullPath(dir))) continue;
            Try(dir, () => Directory.Delete(dir, recursive: true));
        }

        foreach (var file in Directory.EnumerateFiles(paths.Root))
        {
            if (Path.GetFileName(file) == "f3m.sock") continue;
            Try(file, () => File.Delete(file));
        }

        Try("f3m:// link registration", ProtocolRegistration.Unregister);
        Try("Start at login", () => StartAtLogin.Set(false, string.Empty));
        return failed;

        void Try(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                AppLog.Error($"Factory reset could not remove {what}", ex);
                failed.Add(what);
            }
        }
    }

    private static long SizeOf(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
