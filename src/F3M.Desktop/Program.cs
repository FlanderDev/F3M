using Avalonia;
using F3M.Desktop.Core;
using F3M.Desktop.Services;

namespace F3M.Desktop;

public static class Program
{
    public static string[] StartupArgs { get; private set; } = [];

    public static bool StartedMinimized => StartupArgs.Contains("--minimized", StringComparer.Ordinal);

    /// <summary>An f3m:// link given on the command line, when the app was started by one.</summary>
    public static string? StartupLink =>
        StartupArgs.FirstOrDefault(a => a.StartsWith("f3m:", StringComparison.OrdinalIgnoreCase));

    [STAThread]
    public static int Main(string[] args)
    {
        StartupArgs = args;

        // A second launch hands its link (or a "show" request) to the running instance and exits.
        var message = StartupLink ?? "show";
        if (SingleInstance.TrySend(message, new AppPaths())) return 0;

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();
}
