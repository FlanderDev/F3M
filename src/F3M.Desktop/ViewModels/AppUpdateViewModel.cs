using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F3M.Desktop.Core;
using F3M.Desktop.Services;

namespace F3M.Desktop.ViewModels;

/// <summary>Where the app update button is. Each stage has its own button text and click action.</summary>
public enum AppUpdateStage
{
    /// <summary>Not checked yet, or the last check failed. A click checks.</summary>
    Check,
    Checking,

    /// <summary>The newest version runs. A click checks again.</summary>
    UpToDate,

    /// <summary>A newer version exists. A click updates and restarts, or opens the download page.</summary>
    Available,
    Downloading,
}

/// <summary>The update button at the top right: check, then update F3M Desktop and restart into the new version.</summary>
public sealed partial class AppUpdateViewModel : ObservableObject
{
    private readonly AppServices _app;
    private readonly MainViewModel _shell;
    private AppUpdate? _update;

    public AppUpdateViewModel(AppServices app, MainViewModel shell)
    {
        _app = app;
        _shell = shell;
        _checkOnStartup = app.Settings.CheckAppUpdatesOnStartup;
        UpdateText();
    }

    [ObservableProperty]
    private AppUpdateStage _stage = AppUpdateStage.Check;

    [ObservableProperty]
    private string _buttonText = string.Empty;

    [ObservableProperty]
    private string _tip = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Highlights the button when there is something to install.</summary>
    [ObservableProperty]
    private bool _hasUpdate;

    [ObservableProperty]
    private int _progress;

    [ObservableProperty]
    private bool _checkOnStartup;

    partial void OnCheckOnStartupChanged(bool value)
    {
        _app.Settings.CheckAppUpdatesOnStartup = value;
        try
        {
            _app.SaveSettings();
        }
        catch (IOException ex)
        {
            AppLog.Error("Settings could not be saved", ex);
        }
    }

    partial void OnStageChanged(AppUpdateStage value) => UpdateText();

    partial void OnProgressChanged(int value) => UpdateText();

    /// <summary>Called at startup when Check on startup is on. Quiet: a failure only shows on the button.</summary>
    public Task CheckAtStartupAsync() => CheckOnStartup ? CheckAsync() : Task.CompletedTask;

    [RelayCommand]
    private async Task ClickAsync()
    {
        switch (Stage)
        {
            case AppUpdateStage.Check:
            case AppUpdateStage.UpToDate:
                await CheckAsync();
                if (Stage == AppUpdateStage.UpToDate) _shell.Notify($"F3M Desktop {_app.Updater.CurrentVersion} is the newest version.");
                break;

            case AppUpdateStage.Available when _update is not null:
                await UpdateAsync(_update);
                break;
        }
    }

    private async Task CheckAsync()
    {
        Stage = AppUpdateStage.Checking;
        try
        {
            _update = await _app.Updater.CheckAsync(CancellationToken.None);
            Stage = _update is null ? AppUpdateStage.UpToDate : AppUpdateStage.Available;
        }
        catch (Exception ex)
        {
            AppLog.Error("The app update check failed", ex);
            _update = null;
            Stage = AppUpdateStage.Check;
            Tip = "The last check failed: GitHub could not be reached. Click to try again.";
        }
    }

    private async Task UpdateAsync(AppUpdate update)
    {
        if (!update.CanInstall)
        {
            // This copy cannot replace itself; the release page has the new download.
            Launcher.Open(F3M.Shared.Configuration.DesktopDownloadUrl);
            _shell.Notify($"Download F3M Desktop {update.Version} from the page that just opened, and replace this copy with it.");
            return;
        }

        if (_app.Ops.Items.Any(i => !i.IsFinished))
        {
            _shell.Notify("Wait until the running downloads and deploys finish, then update.");
            return;
        }

        Stage = AppUpdateStage.Downloading;
        Progress = 0;
        try
        {
            await _app.Updater.DownloadAsync(update, p => Dispatcher.UIThread.Post(() => Progress = p), CancellationToken.None);
        }
        catch (UserException ex)
        {
            Stage = AppUpdateStage.Available;
            _shell.Notify(ex.Message);
            return;
        }
        catch (Exception ex)
        {
            AppLog.Error("The app update could not be downloaded", ex);
            Stage = AppUpdateStage.Available;
            _shell.Notify("The update could not be downloaded. Try again in a moment.");
            return;
        }

        try
        {
            // Installs the update and starts the new version; this process ends.
            _app.Updater.ApplyAndRestart(update);
        }
        catch (Exception ex) when (ex is UserException or IOException or UnauthorizedAccessException)
        {
            AppLog.Error("The app update could not be installed", ex);
            Stage = AppUpdateStage.Available;
            _shell.Notify(ex is UserException ? ex.Message : "The update could not be installed. See the diagnostics log.");
        }
    }

    private void UpdateText()
    {
        var current = _app.Updater.CurrentVersion;
        var canInstall = _update?.CanInstall == true;
        IsBusy = Stage is AppUpdateStage.Checking or AppUpdateStage.Downloading;
        HasUpdate = Stage is AppUpdateStage.Available or AppUpdateStage.Downloading;
        (ButtonText, Tip) = Stage switch
        {
            AppUpdateStage.Checking => ("Checking…", "Looking for a newer F3M Desktop on GitHub."),
            AppUpdateStage.UpToDate => ("No updates", $"F3M Desktop {current} is the newest version. Click to check again."),
            AppUpdateStage.Available when canInstall => ($"Update to {_update!.Version} and restart",
                $"F3M Desktop {_update.Version} is available (you have {current}). Downloads it, closes the app, installs it and starts it again. Your settings, profiles and downloads are kept."),
            AppUpdateStage.Available => ($"Get version {_update!.Version}",
                $"F3M Desktop {_update.Version} is available (you have {current}). This copy can't update itself, so the download page opens."),
            AppUpdateStage.Downloading => ($"Downloading update… {Progress}%", "The app restarts by itself when the download is done."),
            _ => ("Check for updates", $"You have F3M Desktop {current}. Looks for a newer version on GitHub."),
        };
    }
}
