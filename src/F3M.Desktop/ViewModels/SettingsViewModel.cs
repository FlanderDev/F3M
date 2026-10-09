using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F3M.Desktop.Core;
using F3M.Desktop.Services;

namespace F3M.Desktop.ViewModels;

/// <summary>Server, game folder and launch settings, link registration, and cache and diagnostics actions.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _app;
    private readonly MainViewModel _shell;

    public SettingsViewModel(AppServices app, MainViewModel shell)
    {
        _app = app;
        _shell = shell;
        var s = app.Settings;
        _serverUrl = s.ServerUrl;
        _gameFolder = s.GameFolder;
        _gameExecutable = s.GameExecutable;
        _steamAppId = s.SteamAppId;
        _updateCheckHours = s.UpdateCheckHours.ToString();
        _cacheLimitGb = s.CacheLimitGb.ToString();
        _launchAtLogin = s.StartAtLogin;
        DataFolder = app.Paths.Root;
    }

    [ObservableProperty]
    private string _serverUrl;

    [ObservableProperty]
    private string _gameFolder;

    /// <summary>Relative to the game folder, for example "Game.exe".</summary>
    [ObservableProperty]
    private string _gameExecutable;

    /// <summary>Leave empty to start the executable directly.</summary>
    [ObservableProperty]
    private string _steamAppId;

    [ObservableProperty]
    private string _updateCheckHours;

    [ObservableProperty]
    private string _cacheLimitGb;

    [ObservableProperty]
    private bool _launchAtLogin;

    [ObservableProperty]
    private string _status = string.Empty;

    public string DataFolder { get; }

    [RelayCommand]
    private void OpenServer()
    {
        var url = ServerUrl.Trim();
        if (Launcher.IsOpenable(url) && !Path.IsPathFullyQualified(url)) Launcher.Open(url);
        else Status = "The server address is not a web address.";
    }

    [RelayCommand]
    private void OpenGameFolder()
    {
        var folder = GameFolder.Trim();
        if (Path.IsPathFullyQualified(folder) && Directory.Exists(folder)) Launcher.Open(folder);
        else Status = "The game folder does not exist. Choose it or use Auto-detect.";
    }

    [RelayCommand]
    private async Task PickGameFolderAsync()
    {
        var path = await _app.PickFolder();
        if (!string.IsNullOrEmpty(path)) GameFolder = path;
    }

    /// <summary>Installs found by Auto-detect when there is more than one, to pick from.</summary>
    public ObservableCollection<GameInstallRow> FoundInstalls { get; } = [];

    /// <summary>Auto-detect's result, shown right under its button.</summary>
    [ObservableProperty]
    private string _detectStatus = string.Empty;

    /// <summary>
    /// Finds the game. One install is filled in directly; several are listed to choose from. Nothing is kept until
    /// Save, so the user can check the result first.
    /// </summary>
    [RelayCommand]
    private async Task AutoDetectGameAsync()
    {
        FoundInstalls.Clear();
        DetectStatus = "Looking for the game. This can take a few seconds…";
        List<GameInstall> found;
        try
        {
            found = await Task.Run(() => GameLocator.Find(CancellationToken.None));
        }
        catch (Exception ex)
        {
            AppLog.Error("Game detection failed", ex);
            DetectStatus = "The search failed. Choose the game folder by hand.";
            return;
        }

        if (found.Count == 0)
        {
            DetectStatus = $"{GameLocator.ExecutableName} was not found. Choose the game folder by hand.";
            return;
        }

        if (found.Count == 1)
        {
            UseInstall(found[0]);
            return;
        }

        // Results come in order of confidence: the running game, then Steam, then the folder scan.
        foreach (var install in found) FoundInstalls.Add(new GameInstallRow(install, UseInstall));
        DetectStatus = $"Found {found.Count} installs. Pick the one to manage:";
    }

    private void UseInstall(GameInstall install)
    {
        FoundInstalls.Clear();
        GameFolder = install.Folder;
        GameExecutable = install.Executable;
        if (install.SteamAppId is not null) SteamAppId = install.SteamAppId;

        DetectStatus = $"Using {Markup.Link(install.Folder)} ({install.Source})." +
                       (install.SteamAppId is not null ? $" Steam app ID {install.SteamAppId} filled in." : string.Empty) +
                       " Press Save settings to keep it.";
    }

    [RelayCommand]
    private async Task PickExecutableAsync()
    {
        if (string.IsNullOrWhiteSpace(GameFolder))
        {
            Status = "Choose the game folder first.";
            return;
        }

        var file = await _app.PickFile();
        if (string.IsNullOrEmpty(file)) return;

        var root = Path.GetFullPath(GameFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(file).StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            Status = "Pick the executable inside the game folder.";
            return;
        }

        GameExecutable = Path.GetRelativePath(root, Path.GetFullPath(file)).Replace('\\', '/');
    }

    [RelayCommand]
    private void Save()
    {
        if (!int.TryParse(UpdateCheckHours, out var hours) || hours < 1 || hours > 168)
        {
            Status = "Check for updates every 1 to 168 hours.";
            return;
        }

        if (!int.TryParse(CacheLimitGb, out var limit) || limit < 1)
        {
            Status = "The cache limit must be at least 1 GB.";
            return;
        }

        var s = _app.Settings;
        s.ServerUrl = ServerUrl.Trim().TrimEnd('/');
        s.GameFolder = GameFolder.Trim();
        s.GameExecutable = GameExecutable.Trim();
        s.SteamAppId = SteamAppId.Trim();
        s.UpdateCheckHours = hours;
        s.CacheLimitGb = limit;
        s.StartAtLogin = LaunchAtLogin;

        try
        {
            _app.SaveSettings();
            StartAtLogin.Set(LaunchAtLogin, Program.LaunchPath);
            Status = "Saved.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Settings could not be saved", ex);
            Status = "Settings could not be saved. See the diagnostics log.";
            return;
        }

        _ = _shell.RefreshStateAsync();
        _ = _shell.CheckUpdatesAsync();
    }

    [RelayCommand]
    private void RegisterLinks()
    {
        try
        {
            ProtocolRegistration.Register(Program.LaunchPath);
            Status = "f3m:// links now open F3M Desktop for this user.";
        }
        catch (UserException ex)
        {
            Status = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Error("f3m:// links could not be registered", ex);
            Status = "f3m:// links could not be registered. See the diagnostics log.";
        }
    }

    [RelayCommand]
    private async Task ClearUnusedCacheAsync()
    {
        try
        {
            var (count, bytes) = _app.Downloads.UnusedCache();
            if (count == 0)
            {
                Status = "Nothing unused to clear.";
                return;
            }

            var ok = await _shell.ConfirmAsync(
                "Clear unused cache",
                $"Remove {count} cached version(s), freeing {FileOps.FormatBytes(bytes)}. " +
                "Deployed and pinned versions are kept. They download again when needed.",
                "Clear");
            if (!ok) return;

            _app.Downloads.ClearUnused();
            Status = $"Freed {FileOps.FormatBytes(bytes)}.";
        }
        catch (UserException ex)
        {
            Status = ex.Message;
        }
    }

    /// <summary>
    /// Lists what the app created with its sizes, asks (downloaded mods only when ticked), removes it and restarts.
    /// The game folder is never touched.
    /// </summary>
    [RelayCommand]
    private async Task FactoryResetAsync()
    {
        if (_app.FactoryReset.BlockedReason() is { } blocked)
        {
            Status = blocked;
            return;
        }

        List<ResetItem> items;
        try
        {
            items = await Task.Run(_app.FactoryReset.Survey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Factory reset survey failed", ex);
            Status = "The data folder could not be read. See the diagnostics log.";
            return;
        }

        var game = string.IsNullOrWhiteSpace(_app.Settings.GameFolder) ? "the game folder" : Markup.Link(_app.Settings.GameFolder);
        string Describe(bool includeDownloads)
        {
            var lines = new List<string>
            {
                "Removes everything F3M Desktop created on this computer, then restarts it with default settings. " +
                "Profiles cannot be recovered; share one first if you want to keep it.",
                string.Empty,
                $"**Not touched:** {game} and the mods deployed in it. F3M forgets that it placed them: they stay in the game " +
                "until you remove them by hand, and a later deploy replaces them.",
                string.Empty,
            };

            foreach (var item in items)
            {
                var kept = item.IsDownload && !includeDownloads;
                var size = item.IsRegistration ? "removed" : $"{item.Files} file(s), {FileOps.FormatBytes(item.Bytes)}";
                lines.Add(kept ? $"{item.Label}: {size}, kept" : $"**{item.Label}**: {size}");
                lines.Add("    " + (item.IsRegistration ? item.Location : Markup.Link(item.Location)));
            }

            var removed = items.Where(i => includeDownloads || !i.IsDownload).ToList();
            var keptBytes = items.Where(i => i.IsDownload && !includeDownloads).Sum(i => i.Bytes);
            lines.Add(string.Empty);
            lines.Add($"In total {removed.Sum(i => i.Files)} file(s), {FileOps.FormatBytes(removed.Sum(i => i.Bytes))} are removed." +
                      (keptBytes > 0 ? $" Downloaded mods ({FileOps.FormatBytes(keptBytes)}) are kept and reused." : string.Empty));
            return string.Join("\n", lines);
        }

        var includeDownloads = await _app.ConfirmWithOption("Factory reset", Describe, "Reset and restart", "Also delete downloaded mods");
        if (includeDownloads is null) return;

        Status = "Resetting…";
        var failed = await Task.Run(() => _app.FactoryReset.Run(includeDownloads.Value));
        if (failed.Count > 0)
        {
            await _shell.ConfirmAsync("Factory reset",
                "These could not be removed, probably because another program has them open:\n" +
                string.Join("\n", failed.Select(f => "    " + f)) +
                "\n\nEverything else was removed. Close that program and reset again to finish.", "Restart");
        }

        _app.Restart();
    }

    [RelayCommand]
    private void CopyDiagnostics()
    {
        _app.CopyText($"F3M Desktop {typeof(SettingsViewModel).Assembly.GetName().Version}\n{AppLog.Tail(200)}");
        Status = "Diagnostics copied. Paste them into your bug report.";
    }
}

/// <summary>One install in Auto-detect's list, with the button that picks it.</summary>
public sealed partial class GameInstallRow(GameInstall install, Action<GameInstall> use)
{
    public string Folder => install.Folder;
    public string Source => install.SteamAppId is null ? install.Source : $"{install.Source}, app ID {install.SteamAppId}";

    [RelayCommand]
    private void Use() => use(install);
}
