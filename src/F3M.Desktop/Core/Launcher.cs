using System.Diagnostics;

namespace F3M.Desktop.Core;

/// <summary>
/// Opens what a link points to: a web link in the browser, a folder in the file manager, a file selected in its
/// folder. Only http(s) links and absolute local paths are opened.
/// </summary>
public static class Launcher
{
    /// <summary>Whether <see cref="Open"/> would do something with this target.</summary>
    public static bool IsOpenable(string? target) =>
        !string.IsNullOrWhiteSpace(target) && (IsWebLink(target) || Path.IsPathFullyQualified(target));

    /// <summary>Opens a web link in the browser, a folder in the file manager, or shows a file in its folder.</summary>
    public static void Open(string target)
    {
        try
        {
            if (IsWebLink(target))
            {
                Start(target);
                return;
            }

            if (!Path.IsPathFullyQualified(target)) return;
            var path = Path.GetFullPath(target);
            if (File.Exists(path))
            {
                if (OperatingSystem.IsWindows())
                {
                    var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
                    info.ArgumentList.Add($"/select,{path}");
                    Process.Start(info)?.Dispose();
                    return;
                }

                path = Path.GetDirectoryName(path)!;
            }

            // A path that is gone opens the nearest folder that still exists.
            while (!Directory.Exists(path) && Path.GetDirectoryName(path) is { } parent) path = parent;
            if (Directory.Exists(path)) Start(path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            AppLog.Error($"Could not open {target}", ex);
        }
    }

    private static bool IsWebLink(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static void Start(string target)
    {
        var info = OperatingSystem.IsLinux()
            ? new ProcessStartInfo("xdg-open") { UseShellExecute = false, ArgumentList = { target } }
            : new ProcessStartInfo(target) { UseShellExecute = true };
        Process.Start(info)?.Dispose();
    }
}
