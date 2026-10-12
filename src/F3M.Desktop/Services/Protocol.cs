using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using F3M.Desktop.Core;
using F3M.Shared.Helpers;
using Microsoft.Win32;

namespace F3M.Desktop.Services;

/// <summary>
/// A request from a browser link. Links name the site they come from (<see cref="Server"/>); the app asks the first
/// time it sees a site whether to trust it, and links without a site are confirmed one by one (plan 9.6).
/// </summary>
public abstract record ProtocolRequest
{
    /// <summary>The site the link names, as an origin (https://host[:port]); null for links from older sites.</summary>
    /// <remarks>Claimed by the link, not proven: any page can name any site. It says where to look the mod up.</remarks>
    public string? Server { get; init; }
}

/// <summary>Open the mod's detail in Browse. <c>mod</c> is a group ID.</summary>
public sealed record OpenModRequest(int GroupId) : ProtocolRequest;

/// <summary>Download a mod into the cache, the latest version unless <paramref name="VersionId"/> is given.</summary>
public sealed record DownloadModRequest(int GroupId, int? VersionId) : ProtocolRequest;

/// <summary>Create a local profile from a collection. Does not deploy.</summary>
public sealed record ImportProfileRequest(CollectionLink Collection) : ProtocolRequest;

/// <summary>Parses <c>f3m://v1/&lt;action&gt;?&lt;params&gt;</c> under the rules of plan 9.2. Anything unexpected is rejected.</summary>
public static class ProtocolLinks
{
    private const string Prefix = "f3m://v1/";

    public static bool TryParse(string? uri, out ProtocolRequest? request, out string? error)
    {
        request = null;
        error = null;

        if (string.IsNullOrEmpty(uri) || uri.Length > CollectionLink.MaxUriLength)
        {
            error = "The link is empty or too long.";
            return false;
        }

        if (!uri.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || uri.Contains('#'))
        {
            error = "This link is not one this version of F3M Desktop understands.";
            return false;
        }

        var rest = uri[Prefix.Length..];
        var question = rest.IndexOf('?');
        var action = question < 0 ? rest : rest[..question];
        var query = question < 0 ? string.Empty : rest[(question + 1)..];

        var allowed = action switch
        {
            "open" => new[] { "mod", DesktopLink.ServerParameter },
            "download" => new[] { "mod", "version", DesktopLink.ServerParameter },
            "import-profile" => new[] { "mods", "name", DesktopLink.ServerParameter },
            _ => null,
        };
        if (allowed is null)
        {
            error = $"'{action}' is not an action this app knows.";
            return false;
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0)
            {
                error = "A parameter in the link has no name.";
                return false;
            }

            var key = part[..equals];
            if (!parameters.TryAdd(key, part[(equals + 1)..]))
            {
                error = $"'{key}' is given more than once.";
                return false;
            }

            if (!allowed.Contains(key, StringComparer.Ordinal))
            {
                error = $"'{key}' is not a parameter of '{action}'.";
                return false;
            }
        }

        string? server = null;
        if (parameters.TryGetValue(DesktopLink.ServerParameter, out var rawServer))
        {
            if (!DesktopLink.TryNormalizeServer(Uri.UnescapeDataString(rawServer), out var origin))
            {
                error = "The link names a site that is not a valid web address.";
                return false;
            }
            server = origin;
        }

        if (!TryBuild(action, parameters, out request, out error) || request is null)
            return false;

        request = request with { Server = server };
        return true;
    }

    private static bool TryBuild(string action, Dictionary<string, string> parameters, out ProtocolRequest? request, out string? error)
    {
        request = null;
        error = null;
        switch (action)
        {
            case "open":
                if (!TryId(parameters, "mod", required: true, out var openId, out error)) return false;
                request = new OpenModRequest(openId!.Value);
                return true;

            case "download":
                if (!TryId(parameters, "mod", required: true, out var downloadId, out error)) return false;
                if (!TryId(parameters, "version", required: false, out var versionId, out error)) return false;
                request = new DownloadModRequest(downloadId!.Value, versionId);
                return true;

            default:
                var name = parameters.TryGetValue("name", out var rawName) ? Uri.UnescapeDataString(rawName) : null;
                if (!CollectionLink.TryParse(parameters.GetValueOrDefault("mods"), name, out var link, out error) || link is null)
                    return false;
                request = new ImportProfileRequest(link);
                return true;
        }
    }

    private static bool TryId(Dictionary<string, string> parameters, string key, bool required, out int? id, out string? error)
    {
        id = null;
        error = null;
        if (!parameters.TryGetValue(key, out var raw))
        {
            if (required) error = $"'{key}' is required.";
            return !required;
        }

        if (raw.Length is 0 or > 10 || !raw.All(char.IsAsciiDigit) || !int.TryParse(raw, out var value) || value <= 0)
        {
            error = $"'{key}' must be a positive whole number.";
            return false;
        }

        id = value;
        return true;
    }
}

/// <summary>Registers f3m:// links for the current user only, so no admin rights are needed (plan 9.4).</summary>
public static class ProtocolRegistration
{
    public static void Register(string executable)
    {
        if (OperatingSystem.IsWindows())
            RegisterWindows(executable);
        else if (OperatingSystem.IsLinux())
            RegisterLinux(executable);
        else
            throw new UserException("Link registration is not supported on this system.");
    }

    /// <summary>
    /// Called at every start: registers f3m:// links for this executable unless they already open it, so links keep
    /// working after an install, a move or a factory reset. Returns false when registration failed (it is logged).
    /// </summary>
    public static bool EnsureRegistered(string executable)
    {
        if (string.IsNullOrEmpty(executable)) return false;

        try
        {
            if (IsRegisteredFor(executable)) return true;
            Register(executable);
            AppLog.Info($"Registered f3m:// links for {executable}.");
            return true;
        }
        catch (Exception ex) when (ex is UserException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Error("f3m:// links could not be registered at startup", ex);
            return false;
        }
    }

    /// <summary>True when f3m:// links already start exactly this executable.</summary>
    public static bool IsRegisteredFor(string executable)
    {
        if (OperatingSystem.IsWindows())
        {
            using var command = Registry.CurrentUser.OpenSubKey(WindowsKey + @"\shell\open\command");
            return command?.GetValue(string.Empty) as string == WindowsCommand(executable);
        }

        if (OperatingSystem.IsLinux())
        {
            return File.Exists(LinuxDesktopFile)
                   && File.ReadAllText(LinuxDesktopFile) == LinuxEntry(executable)
                   && Query("xdg-mime", "query", "default", "x-scheme-handler/f3m") == "f3m-desktop.desktop";
        }

        return false;
    }

    private const string WindowsKey = @"Software\Classes\f3m";

    private static string LinuxDesktopFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "applications", "f3m-desktop.desktop");

    public static bool IsRegistered()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.OpenSubKey(WindowsKey);
            return key is not null;
        }

        return OperatingSystem.IsLinux() && File.Exists(LinuxDesktopFile);
    }

    /// <summary>Removes what <see cref="Register"/> added. Nothing happens when links are not registered.</summary>
    public static void Unregister()
    {
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(WindowsKey, throwOnMissingSubKey: false);
        }
        else if (OperatingSystem.IsLinux() && File.Exists(LinuxDesktopFile))
        {
            File.Delete(LinuxDesktopFile);
            Run("update-desktop-database", Path.GetDirectoryName(LinuxDesktopFile)!);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RegisterWindows(string executable)
    {
        using var root = Registry.CurrentUser.CreateSubKey(WindowsKey);
        root.SetValue(string.Empty, "URL:F3M Protocol");
        root.SetValue("URL Protocol", string.Empty);
        using (var icon = root.CreateSubKey("DefaultIcon")) icon.SetValue(string.Empty, $"\"{executable}\",0");
        using var command = root.CreateSubKey(@"shell\open\command");
        command.SetValue(string.Empty, WindowsCommand(executable));
    }

    private static string WindowsCommand(string executable) => $"\"{executable}\" \"%1\"";

    private static string LinuxEntry(string executable) =>
        "[Desktop Entry]\n" +
        "Type=Application\n" +
        "Name=F3M Desktop\n" +
        $"Exec=\"{executable}\" %u\n" +
        "Terminal=false\n" +
        "NoDisplay=true\n" +
        "Categories=Game;\n" +
        "MimeType=x-scheme-handler/f3m;\n";

    private static void RegisterLinux(string executable)
    {
        var folder = Path.GetDirectoryName(LinuxDesktopFile)!;
        Directory.CreateDirectory(folder);
        File.WriteAllText(LinuxDesktopFile, LinuxEntry(executable));

        if (!Run("xdg-mime", "default", "f3m-desktop.desktop", "x-scheme-handler/f3m"))
            throw new UserException("xdg-mime is not installed, so the link could not be registered.");
        Run("update-desktop-database", folder);
    }

    /// <summary>The trimmed output of a command; null when it can't run or fails.</summary>
    private static string? Query(string fileName, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(fileName) { UseShellExecute = false, RedirectStandardOutput = true };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static bool Run(string fileName, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(fileName) { UseShellExecute = false };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            process?.WaitForExit(5000);
            return process is not null && process.ExitCode == 0;
        }
        catch (Win32Exception ex)
        {
            AppLog.Error($"{fileName} is not available", ex);
            return false;
        }
    }
}

/// <summary>Start at login, off by default. The app starts minimised to the tray (plan 3.5, P6).</summary>
public static class StartAtLogin
{
    private const string WindowsValue = "F3M Desktop";

    private static string LinuxFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart", "f3m-desktop.desktop");

    public static bool IsEnabled()
    {
        if (OperatingSystem.IsWindows())
        {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return run?.GetValue(WindowsValue) is not null;
        }

        return OperatingSystem.IsLinux() && File.Exists(LinuxFile);
    }

    public static void Set(bool enabled, string executable)
    {
        if (OperatingSystem.IsWindows())
            SetWindows(enabled, executable);
        else if (OperatingSystem.IsLinux())
            SetLinux(enabled, executable);
    }

    [SupportedOSPlatform("windows")]
    private static void SetWindows(bool enabled, string executable)
    {
        using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
            run.SetValue(WindowsValue, $"\"{executable}\" --minimized");
        else
            run.DeleteValue(WindowsValue, throwOnMissingValue: false);
    }

    private static void SetLinux(bool enabled, string executable)
    {
        var file = LinuxFile;
        if (!enabled)
        {
            if (File.Exists(file)) File.Delete(file);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file,
            "[Desktop Entry]\n" +
            "Type=Application\n" +
            "Name=F3M Desktop\n" +
            $"Exec=\"{executable}\" --minimized\n" +
            "X-GNOME-Autostart-enabled=true\n");
    }
}
