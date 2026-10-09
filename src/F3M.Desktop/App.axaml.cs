using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using F3M.Desktop.Core;
using F3M.Desktop.Services;
using F3M.Desktop.Tray;
using F3M.Desktop.ViewModels;
using F3M.Desktop.Views;

namespace F3M.Desktop;

/// <summary>Start-up and wiring: services, the window, the tray, single-instance listening, and Exit.</summary>
public partial class App : Application
{
    private AppServices? _services;
    private TrayMenu? _tray;
    private CancellationTokenSource? _listening;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Page templates are matched by view-model type, so they belong in DataTemplates, not in resources.
        DataTemplates.AddRange((DataTemplates)AvaloniaXamlLoader.Load(new Uri("avares://F3M.Desktop/Views/Pages.axaml")));
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var services = new AppServices();
            _services = services;

            var main = new MainWindow { Icon = TrayMenu.CreateIcon() };
            var shell = new MainViewModel(services);
            main.DataContext = shell;
            desktop.MainWindow = main;

            ConnectUi(services, shell, main, desktop);

            _listening = new CancellationTokenSource();
            _ = ListenAsync(services, shell, _listening.Token);
            _ = StartupAsync(services, shell, Program.StartupLink);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ConnectUi(AppServices services, MainViewModel shell, MainWindow main, IClassicDesktopStyleApplicationLifetime desktop)
    {
        services.Confirm = (title, body, ok) => ConfirmWindow.AskAsync(main, title, body, ok);
        services.ConfirmWithOption = (title, body, ok, option) =>
            ConfirmWindow.AskWithOptionAsync(main, title, body, ok, option, destructive: true);
        services.Restart = () => Restart(services, desktop);
        services.Notify = shell.Notify;
        services.CopyText = text => _ = main.Clipboard?.SetTextAsync(text);
        services.PickFolder = async () =>
        {
            var folders = await main.StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions { Title = "Choose the game folder", AllowMultiple = false });
            return folders.Count > 0 ? folders[0].Path.LocalPath : null;
        };
        services.PickFile = async () =>
        {
            var files = await main.StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions { Title = "Choose the game executable", AllowMultiple = false });
            return files.Count > 0 ? files[0].Path.LocalPath : null;
        };
        services.OpenConfig = async groupId =>
        {
            try
            {
                var mod = services.Deploy.LoadState().Mods.FirstOrDefault(m => m.GroupId == groupId);
                if (mod is null)
                {
                    shell.Notify("Deploy this mod before editing its config.");
                    return;
                }

                var editor = new ConfigViewModel(services);
                editor.Open(mod);
                await new ConfigWindow { DataContext = editor }.ShowDialog(main);
            }
            catch (UserException ex)
            {
                shell.Notify(ex.Message);
            }
        };

        // Closing the window hides it to the tray. Exit is the only way out (plan 3.5).
        main.Closing += (_, e) =>
        {
            if (services.Exiting) return;
            e.Cancel = true;
            main.Hide();
        };
        main.Opened += (_, _) =>
        {
            if (Program.StartedMinimized) main.Hide();
        };

        shell.ShowRequested += (_, _) => Dispatcher.UIThread.Post(() => Show(main));
        shell.StateChanged += (_, _) => RefreshTray(shell);

        _tray = new TrayMenu(
            this,
            open: () => Show(main),
            play: () => _ = shell.PlayCommand.ExecuteAsync(null),
            switchTo: id => SwitchFromTray(shell, id),
            exit: () => _ = ExitAsync(services, main, desktop));

        desktop.Exit += (_, _) =>
        {
            _listening?.Cancel();
            _tray?.Dispose();
            services.Dispose();
        };

        RefreshTray(shell);
    }

    private void RefreshTray(MainViewModel shell)
    {
        var profiles = shell.ProfileChoices.Select(p => (p.Id, p.Name)).ToList();
        var state = shell.GameRunning ? "game running" : shell.CanDeploy ? "changes pending" : "up to date";
        _tray?.Refresh(profiles, shell.ActiveProfile?.Id, $"F3M Desktop: {state}");
    }

    private static void SwitchFromTray(MainViewModel shell, string profileId)
    {
        var profile = shell.ProfileChoices.FirstOrDefault(p => p.Id == profileId);
        if (profile is not null) shell.ProfileSelection = profile;
    }

    private static void Show(Window window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>
    /// Starts a new instance and shuts this one down. The listener stops first and the new instance is told not to
    /// hand over to this one, so it starts on its own even while this one is still closing.
    /// </summary>
    private void Restart(AppServices services, IClassicDesktopStyleApplicationLifetime desktop)
    {
        services.Exiting = true;
        _listening?.Cancel();
        _tray?.Dispose();
        try
        {
            var info = new ProcessStartInfo(Program.LaunchPath) { UseShellExecute = false };
            info.ArgumentList.Add(Program.RestartedArg);
            Process.Start(info)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Error("The app could not restart itself", ex);
        }

        desktop.Shutdown();
    }

    /// <summary>Exit asks first when a deploy is running, cancels it (which rolls it back), and waits for that to finish.</summary>
    private async Task ExitAsync(AppServices services, MainWindow main, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var running = services.Ops.Items.Where(i => i.Kind == "Deploy" && !i.IsFinished).ToList();
        if (running.Count > 0)
        {
            var ok = await ConfirmWindow.AskAsync(main, "Exit F3M Desktop",
                "A deploy is in progress. Exit anyway? It will be rolled back.", "Exit anyway");
            if (!ok) return;

            foreach (var item in running) item.Cancel();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (running.Any(i => !i.IsFinished) && DateTime.UtcNow < deadline)
                await Task.Delay(200);
        }

        services.Exiting = true;
        _tray?.Dispose();
        desktop.Shutdown();
    }

    /// <summary>
    /// Accepts second launches and f3m:// links from other instances. Restarts after a failure, so one bad
    /// request never leaves the app unable to take links.
    /// </summary>
    private static async Task ListenAsync(AppServices services, MainViewModel shell, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SingleInstance.ListenAsync(services.Paths,
                    message => Dispatcher.UIThread.Post(() => _ = shell.HandleMessageAsync(message)), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLog.Error("The single-instance listener stopped; restarting it", ex);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private static async Task StartupAsync(AppServices services, MainViewModel shell, string? link)
    {
        // An interrupted deploy (crash or power loss) is undone before anything reads the game folder.
        try
        {
            if (!await Task.Run(services.Deploy.Rollback))
                shell.Notify("An interrupted deploy could not be fully undone: a file in the game folder is in use. " +
                             "Close the game and press Deploy to finish undoing it.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup recovery of an interrupted deploy failed", ex);
        }

        await shell.RefreshStateAsync();
        await shell.CheckUpdatesAsync();

        // The update check has loaded the newest index when it could; Browse uses it, or loads it now.
        await shell.BrowsePage.ReloadAsync();
        if (link is not null) await shell.HandleMessageAsync(link);
    }
}
