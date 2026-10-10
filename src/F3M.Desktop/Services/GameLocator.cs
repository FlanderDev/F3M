using System.Diagnostics;
using System.Text.RegularExpressions;
using F3M.Desktop.Core;
using Microsoft.Win32;

namespace F3M.Desktop.Services;

/// <summary>A game install found on this computer. <see cref="SteamAppId"/> is set when Steam manages it.</summary>
public sealed record GameInstall(string Folder, string Executable, string? SteamAppId, string Source);

/// <summary>
/// Looks for the game on this computer: a running game first, then every Steam library, then a bounded scan of the
/// usual install places. Read-only; it never changes settings.
/// </summary>
public static partial class GameLocator
{
    public const string ExecutableName = "SecretFlasherManaka.exe";

    /// <summary>
    /// How deep the folder scan goes below a user folder (Downloads, Desktop) and below a drive root, and how many
    /// folders it looks at in total. Drive roots stay shallow: D:\Games\SFM is found, a whole disk is not walked.
    /// </summary>
    private const int ScanDepth = 3;
    private const int DriveScanDepth = 2;
    private const int ScanBudget = 40_000;

    private static readonly string[] SkippedFolders =
        ["Windows", "$Recycle.Bin", "System Volume Information", "ProgramData", "node_modules", ".git", "proc", "sys", "dev"];

    public static List<GameInstall> Find(CancellationToken ct)
    {
        var found = new List<GameInstall>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        void Add(string exePath, string? appId, string source)
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(exePath));
            if (folder is null || !seen.Add(folder)) return;
            found.Add(new GameInstall(folder, Path.GetFileName(exePath), appId, source));
        }

        Try(() => FromRunningGame(Add));
        ct.ThrowIfCancellationRequested();
        Try(() => FromSteam(Add, ct));
        ct.ThrowIfCancellationRequested();
        Try(() => FromFolderScan(Add, ct));
        return found;
    }

    private static void Try(Action step)
    {
        try
        {
            step();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Error("Game detection step failed", ex);
        }
    }

    private static void FromRunningGame(Action<string, string?, string> add)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExecutableName)))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } path && File.Exists(path)) add(path, null, "running game");
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Another user's process, or it exited meanwhile.
                }
            }
        }
    }

    /// <summary>Reads Steam's library list, then each library's app manifests, so the Steam app ID comes along.</summary>
    private static void FromSteam(Action<string, string?, string> add, CancellationToken ct)
    {
        foreach (var library in SteamLibraries())
        {
            ct.ThrowIfCancellationRequested();
            var steamApps = Path.Combine(library, "steamapps");
            var common = Path.Combine(steamApps, "common");
            if (!Directory.Exists(common)) continue;

            foreach (var manifest in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(manifest);
                var appId = AppIdPattern().Match(text);
                var installDir = InstallDirPattern().Match(text);
                if (!appId.Success || !installDir.Success) continue;

                var exe = FindExe(Path.Combine(common, installDir.Groups[1].Value));
                if (exe is not null) add(exe, appId.Groups[1].Value, "Steam library");
            }

            // Games copied into a library by hand have no manifest.
            foreach (var dir in Directory.EnumerateDirectories(common))
                if (FindExe(dir) is { } exe) add(exe, null, "Steam library");
        }
    }

    private static string? FindExe(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        var direct = Path.Combine(folder, ExecutableName);
        if (File.Exists(direct)) return direct;

        // Some installs keep the game one folder down.
        foreach (var dir in Directory.EnumerateDirectories(folder))
        {
            var nested = Path.Combine(dir, ExecutableName);
            if (File.Exists(nested)) return nested;
        }

        return null;
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string userPath) roots.Add(userPath);
            if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) is string machinePath) roots.Add(machinePath);
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots.Add(Path.Combine(home, ".steam", "steam"));
            roots.Add(Path.Combine(home, ".steam", "root"));
            roots.Add(Path.Combine(home, ".local", "share", "Steam"));
            roots.Add(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));
            roots.Add(Path.Combine(home, "snap", "steam", "common", ".local", "share", "Steam"));
        }

        var libraries = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var root in roots.Where(Directory.Exists))
        {
            libraries.Add(Path.GetFullPath(root));
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match match in LibraryPathPattern().Matches(File.ReadAllText(vdf)))
            {
                var path = match.Groups[1].Value.Replace(@"\\", @"\");
                if (Directory.Exists(path)) libraries.Add(Path.GetFullPath(path));
            }
        }

        return libraries;
    }

    /// <summary>A breadth-first scan of the usual install places, a few levels deep and with a folder budget.</summary>
    private static void FromFolderScan(Action<string, string?, string> add, CancellationToken ct)
    {
        var queue = new Queue<(string Path, int Depth)>();
        foreach (var (root, depth) in ScanRoots()) queue.Enqueue((root, ScanDepth - depth));

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };
        var visited = 0;
        while (queue.Count > 0 && visited++ < ScanBudget)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = queue.Dequeue();
            try
            {
                var exe = Path.Combine(dir, ExecutableName);
                if (File.Exists(exe)) add(exe, null, "folder scan");
                if (depth >= ScanDepth) continue;

                foreach (var child in Directory.EnumerateDirectories(dir, "*", options))
                {
                    var name = Path.GetFileName(child);
                    if (name.StartsWith('.') || SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    queue.Enqueue((child, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable folders are skipped.
            }
        }
    }

    /// <summary>Each search root with how many levels below it are scanned.</summary>
    private static IEnumerable<(string Root, int Depth)> ScanRoots()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new List<(string, int)>
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.Desktop), ScanDepth),
            (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), ScanDepth),
            (Path.Combine(home, "Downloads"), ScanDepth),
            (Path.Combine(home, "Games"), ScanDepth),
        };

        if (OperatingSystem.IsWindows())
        {
            roots.Add((Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), DriveScanDepth));
            roots.Add((Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), DriveScanDepth));
            foreach (var drive in DriveInfo.GetDrives().Where(d => d is { DriveType: DriveType.Fixed, IsReady: true }))
                roots.Add((drive.RootDirectory.FullName, DriveScanDepth));
        }
        else
        {
            roots.Add((home, DriveScanDepth));
            roots.Add(("/opt", DriveScanDepth));
            roots.Add(("/mnt", ScanDepth));
            roots.Add(("/media", ScanDepth));
        }

        return roots.Where(r => r.Item1.Length > 0 && Directory.Exists(r.Item1)).DistinctBy(r => r.Item1, StringComparer.OrdinalIgnoreCase);
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex LibraryPathPattern();

    [GeneratedRegex("\"appid\"\\s+\"(\\d+)\"")]
    private static partial Regex AppIdPattern();

    [GeneratedRegex("\"installdir\"\\s+\"([^\"]+)\"")]
    private static partial Regex InstallDirPattern();
}
