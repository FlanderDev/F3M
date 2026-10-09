using System.Net;
using F3M.Desktop.Services;

namespace F3M.Desktop.Core;

/// <summary>Where the app keeps its data. Windows: %LocalAppData%\F3M. Linux: $XDG_DATA_HOME/f3m (default ~/.local/share/f3m).</summary>
public sealed class AppPaths
{
    public AppPaths()
    {
        var overridePath = Environment.GetEnvironmentVariable("F3M_DATA_DIR");
        Root = string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                OperatingSystem.IsWindows() ? "F3M" : "f3m")
            : overridePath;
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string Logs => Path.Combine(Root, "logs");
    public string Catalog => Path.Combine(Root, "catalog");
    public string Profiles => Path.Combine(Root, "profiles");
    public string State => Path.Combine(Root, "state");
    public string Cache => Path.Combine(Root, "cache");
    public string Downloads => Path.Combine(Root, "downloads");
    public string Journal => Path.Combine(Root, "journal");
    public string ConfigBackups => Path.Combine(Root, "config-backups");

    /// <summary>A file under the catalog folder, mirroring the server layout, e.g. "mods/45/678.json".</summary>
    public string CatalogFile(string relative) =>
        Path.Combine(Catalog, relative.Replace('/', Path.DirectorySeparatorChar));
}

/// <summary>Diagnostics log in the data folder, rotated at 5 MB. Never throws.</summary>
public static class AppLog
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _file;

    public static void Init(AppPaths paths)
    {
        Directory.CreateDirectory(paths.Logs);
        _file = Path.Combine(paths.Logs, "app.log");
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    public static string Tail(int lines)
    {
        lock (Gate)
        {
            try
            {
                if (_file is null || !File.Exists(_file)) return string.Empty;
                var all = File.ReadAllLines(_file);
                return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }
    }

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            try
            {
                if (_file is null) return;
                if (File.Exists(_file) && new FileInfo(_file).Length > MaxBytes)
                    File.Move(_file, _file + ".1", overwrite: true);
                File.AppendAllText(_file, $"{DateTimeOffset.Now:O} {level} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>
/// The services the view models use. Built once in <see cref="App"/>. UI-only actions (dialogs, folder picker,
/// clipboard, toasts) are delegates set by the window, so the services stay free of Avalonia types.
/// </summary>
public sealed class AppServices : IDisposable
{
    public AppPaths Paths { get; } = new();
    public AppSettings Settings { get; private set; } = new();
    public HttpClient Http { get; }
    public Catalog Catalog { get; }
    public OperationQueue Ops { get; } = new();
    public Downloads Downloads { get; }
    public Profiles Profiles { get; }
    public Deploy Deploy { get; }
    public Game Game { get; }
    public FactoryReset FactoryReset { get; }
    public AppUpdater Updater { get; }

    /// <summary>Set when the user chooses Exit, so closing the main window no longer hides it to the tray.</summary>
    public bool Exiting { get; set; }

    public Func<string, string, string, Task<bool>> Confirm { get; set; } = (_, _, _) => Task.FromResult(true);

    /// <summary>A confirmation with a check box: title, text for the box state, OK text, box text. Null when cancelled.</summary>
    public Func<string, Func<bool, string>, string, string, Task<bool?>> ConfirmWithOption { get; set; } =
        (_, _, _, _) => Task.FromResult<bool?>(null);

    /// <summary>
    /// Gets ready for the process to end without the normal shutdown: stops the link listener and removes the tray
    /// icon. Set by the app lifetime; used before the app updater takes over.
    /// </summary>
    public Action PrepareForExit { get; set; } = () => { };

    /// <summary>Closes this instance and starts a fresh one, set by the app lifetime.</summary>
    public Action Restart { get; set; } = () => { };
    public Func<Task<string?>> PickFolder { get; set; } = () => Task.FromResult<string?>(null);
    public Func<Task<string?>> PickFile { get; set; } = () => Task.FromResult<string?>(null);
    public Action<string> Notify { get; set; } = _ => { };
    public Action<string> CopyText { get; set; } = _ => { };

    /// <summary>Opens the config editor for a mod group, set by the window that owns the editor.</summary>
    public Func<int, Task> OpenConfig { get; set; } = _ => Task.CompletedTask;

    public AppServices()
    {
        AppLog.Init(Paths);
        Settings = LoadSettings();

        // Cookie-less and anonymous (plan 2.2). Timeouts are per request, because downloads can take a long time.
        var handler = new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        Http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        Http.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"F3M-Desktop/{typeof(AppServices).Assembly.GetName().Version}");

        Catalog = new Catalog(this);
        Downloads = new Downloads(this);
        Profiles = new Profiles(this);
        Deploy = new Deploy(this);
        Game = new Game(this);
        FactoryReset = new FactoryReset(this);
        Updater = new AppUpdater(this);
    }

    /// <summary>Stable per game folder, used for profile and state folders.</summary>
    public string GameId => GameIdFor(Settings.GameFolder);

    public static string GameIdFor(string gameFolder) =>
        FileOps.Sha256Text(Path.GetFullPath(string.IsNullOrWhiteSpace(gameFolder) ? "none" : gameFolder))[..16];

    /// <summary>Absolute URL for a server-relative path. Throws a user message when no server is set.</summary>
    public Uri ServerUri(string relative)
    {
        if (string.IsNullOrWhiteSpace(Settings.ServerUrl))
            throw new UserException("Set the server address in Settings first.");
        try
        {
            var baseUri = new Uri(Settings.ServerUrl.TrimEnd('/') + "/", UriKind.Absolute);
            return new Uri(baseUri, relative.TrimStart('/'));
        }
        catch (UriFormatException)
        {
            throw new UserException("The server address in Settings is not a valid URL.");
        }
    }

    public void SaveSettings() => FileOps.WriteJsonAtomic(Paths.SettingsFile, Settings);

    public void Dispose() => Http.Dispose();

    private AppSettings LoadSettings()
    {
        try
        {
            return FileOps.ReadJson<AppSettings>(Paths.SettingsFile) ?? new AppSettings();
        }
        catch (System.Text.Json.JsonException ex)
        {
            // Keep the broken file for inspection and start with defaults.
            AppLog.Error("settings.json could not be read; starting with defaults", ex);
            File.Move(Paths.SettingsFile, Paths.SettingsFile + ".broken", overwrite: true);
            return new AppSettings();
        }
    }
}
