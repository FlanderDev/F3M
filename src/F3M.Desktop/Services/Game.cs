using System.ComponentModel;
using System.Diagnostics;
using F3M.Desktop.Core;

namespace F3M.Desktop.Services;

/// <summary>The game folder: validation, running detection, preflight checks and launching (plan 6.2, 7).</summary>
public sealed class Game(AppServices app)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(app.Settings.GameFolder) && Directory.Exists(app.Settings.GameFolder);

    /// <summary>The full path of the game folder. Throws a user message when none is set.</summary>
    public string RootOrThrow()
    {
        if (!IsConfigured) throw new UserException("Choose the game folder in Settings first.");
        return Path.GetFullPath(app.Settings.GameFolder);
    }

    public bool HasExecutable =>
        IsConfigured && !string.IsNullOrWhiteSpace(app.Settings.GameExecutable)
                     && File.Exists(Path.Combine(RootOrThrow(), app.Settings.GameExecutable));

    public bool HasBepInEx => IsConfigured && Directory.Exists(Path.Combine(RootOrThrow(), "BepInEx"));

    /// <summary>Process name used to detect the game while it runs, without the extension.</summary>
    public string ProcessName =>
        string.IsNullOrWhiteSpace(app.Settings.GameExecutable)
            ? string.Empty
            : Path.GetFileNameWithoutExtension(app.Settings.GameExecutable);

    public bool IsRunning()
    {
        var name = ProcessName;
        if (name.Length == 0) return false;

        var processes = Process.GetProcessesByName(name);
        var running = processes.Length > 0;
        foreach (var process in processes) process.Dispose();
        return running;
    }

    /// <summary>Reasons Play must not start. Empty when the game can start.</summary>
    public List<string> PreflightProblems(bool profileHasPlugins)
    {
        var problems = new List<string>();
        if (!IsConfigured)
        {
            problems.Add("Choose the game folder in Settings.");
            return problems;
        }

        if (string.IsNullOrWhiteSpace(app.Settings.SteamAppId) && !HasExecutable)
            problems.Add("Set the game executable in Settings.");
        if (profileHasPlugins && !HasBepInEx)
            problems.Add("BepInEx is missing from the game folder. Install BepInEx first.");
        if (File.Exists(Path.Combine(app.Paths.Journal, "current.ndjson")))
            problems.Add("A deploy was interrupted. Deploy again to finish it.");
        if (app.Ops.Items.Any(i => i.Kind == "Deploy" && !i.IsFinished))
            problems.Add("A deploy is in progress.");
        return problems;
    }

    public void Launch()
    {
        if (!string.IsNullOrWhiteSpace(app.Settings.SteamAppId))
        {
            OpenExternal($"steam://rungameid/{app.Settings.SteamAppId.Trim()}");
            return;
        }

        var root = RootOrThrow();
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(root, app.Settings.GameExecutable))
            {
                WorkingDirectory = root,
                UseShellExecute = false,
            });
        }
        catch (Win32Exception ex)
        {
            throw new UserException($"The game could not be started: {ex.Message}");
        }
    }

    /// <summary>Opens a URL or a steam:// link with the system handler.</summary>
    public static void OpenExternal(string target)
    {
        try
        {
            if (OperatingSystem.IsLinux())
                Process.Start(new ProcessStartInfo("xdg-open", target) { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            AppLog.Error($"Could not open {target}", ex);
            throw new UserException("Could not open the link. Check that Steam is installed.");
        }
    }
}
