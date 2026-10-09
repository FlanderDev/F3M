using Avalonia;
using F3M.Desktop.Core;
using F3M.Desktop.Services;
using Velopack;

namespace F3M.Desktop;

public static class Program
{
    public static string[] StartupArgs { get; private set; } = [];

    public static bool StartedMinimized => StartupArgs.Contains("--minimized", StringComparer.Ordinal);

    /// <summary>An f3m:// link given on the command line, when the app was started by one.</summary>
    public static string? StartupLink =>
        StartupArgs.FirstOrDefault(a => a.StartsWith("f3m:", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The path other programs should start the app with. Inside an AppImage the process runs from a mount that
    /// changes on every run, so the AppImage file itself ($APPIMAGE) is the stable path.
    /// </summary>
    public static string LaunchPath =>
        OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage
            ? appImage
            : Environment.ProcessPath ?? string.Empty;

    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: Velopack calls the app with install, update and uninstall hooks and exits here for those.
        VelopackApp.Build().Run();

        StartupArgs = args;

        // A second launch hands its link (or a "show" request) to the running instance and exits.
        var message = StartupLink ?? "show";
        if (SingleInstance.TrySend(message, new AppPaths())) return 0;

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();
}
