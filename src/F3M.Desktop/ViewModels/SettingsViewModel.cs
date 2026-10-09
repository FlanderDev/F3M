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
    private async Task PickGameFolderAsync()
    {
        var path = await _app.PickFolder();
        if (!string.IsNullOrEmpty(path)) GameFolder = path;
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
            StartAtLogin.Set(LaunchAtLogin, Environment.ProcessPath ?? string.Empty);
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
            ProtocolRegistration.Register(Environment.ProcessPath ?? string.Empty);
            Status = "f3m:// links now open F3M Desktop for this user.";
        }
        catch (UserException ex)
        {
            Status = ex.Message;
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

    [RelayCommand]
    private void CopyDiagnostics()
    {
        _app.CopyText($"F3M Desktop {typeof(SettingsViewModel).Assembly.GetName().Version}\n{AppLog.Tail(200)}");
        Status = "Diagnostics copied. Paste them into your bug report.";
    }
}
