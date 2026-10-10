using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F3M.Desktop.Core;
using F3M.Desktop.Services;
using F3M.Shared.Models;

namespace F3M.Desktop.ViewModels;

/// <summary>The window shell: navigation, the top bar (profile, Deploy, Play), the status line, and link handling.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _app;
    private readonly DispatcherTimer _gameTimer;
    private readonly DispatcherTimer _updateTimer;
    private readonly Queue<DateTimeOffset> _linkTimes = new();
    private string _lastLink = string.Empty;
    private DateTimeOffset _lastLinkAt;
    private bool _syncing;
    private List<IndexGroup> _cacheUpdates = [];

    public MainViewModel(AppServices app)
    {
        _app = app;
        BrowsePage = new BrowseViewModel(app, this);
        LibraryPage = new LibraryViewModel(app, this);
        ProfilesPage = new ProfilesViewModel(app, this);
        DownloadsPage = new DownloadsViewModel(app);
        SettingsPage = new SettingsViewModel(app, this);
        AppUpdate = new AppUpdateViewModel(app, this);
        CurrentPage = LibraryPage;
        DeployBeforePlay = app.Settings.DeployBeforePlay;

        _app.Catalog.LoadCached();
        _app.Downloads.CleanStaging();

        _gameTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _gameTimer.Tick += (_, _) =>
        {
            GameRunning = _app.Game.IsRunning();
            StatusText = BuildStatusText();
        };
        _gameTimer.Start();

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(Math.Max(1, app.Settings.UpdateCheckHours)) };
        _updateTimer.Tick += async (_, _) => await CheckUpdatesAsync();
        _updateTimer.Start();
    }

    /// <summary>Raised after profiles or the deployed state change, so other pages can refresh.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised when a second launch or a link asks for the window to be shown.</summary>
    public event EventHandler? ShowRequested;

    public BrowseViewModel BrowsePage { get; }
    public LibraryViewModel LibraryPage { get; }
    public ProfilesViewModel ProfilesPage { get; }
    public DownloadsViewModel DownloadsPage { get; }
    public SettingsViewModel SettingsPage { get; }

    /// <summary>The F3M Desktop update button at the top right.</summary>
    public AppUpdateViewModel AppUpdate { get; }

    [ObservableProperty]
    private ObservableObject? _currentPage;

    /// <summary>The profile that is deployed, or the first profile when nothing is deployed yet.</summary>
    [ObservableProperty]
    private ProfileDef? _activeProfile;

    /// <summary>What the top-bar selector shows. Choosing another profile switches to it.</summary>
    [ObservableProperty]
    private ProfileDef? _profileSelection;

    public ObservableCollection<ProfileDef> ProfileChoices { get; } = [];

    [ObservableProperty]
    private string _pendingText = string.Empty;

    [ObservableProperty]
    private bool _canDeploy;

    [ObservableProperty]
    private string _playNote = string.Empty;

    [ObservableProperty]
    private bool _gameRunning;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private string _updateBanner = string.Empty;

    /// <summary>Whether downloaded mods have newer versions; shows the Download updates button.</summary>
    [ObservableProperty]
    private bool _hasUpdates;

    [ObservableProperty]
    private bool _deployBeforePlay;

    partial void OnProfileSelectionChanged(ProfileDef? value)
    {
        if (_syncing || value is null || value.Id == ActiveProfile?.Id) return;
        _ = SwitchToAsync(value);
    }

    partial void OnDeployBeforePlayChanged(bool value)
    {
        _app.Settings.DeployBeforePlay = value;
        SaveSettingsSafe();
        _ = RefreshStateAsync();
    }

    [RelayCommand]
    private void ShowBrowse()
    {
        CurrentPage = BrowsePage;
        // Normally loaded at startup; this covers a startup that could not reach the server.
        if (!BrowsePage.HasCards && !BrowsePage.IsLoading) _ = BrowsePage.ReloadAsync();
    }

    [RelayCommand]
    private void ShowLibrary() => CurrentPage = LibraryPage;

    [RelayCommand]
    private void OpenRepository() => Launcher.Open(F3M.Shared.Configuration.RepositoryUrl);

    [RelayCommand]
    private void ShowProfiles() => CurrentPage = ProfilesPage;

    [RelayCommand]
    private void ShowDownloads() => CurrentPage = DownloadsPage;

    [RelayCommand]
    private void ShowSettings() => CurrentPage = SettingsPage;

    [RelayCommand]
    private async Task DeployNowAsync()
    {
        if (ActiveProfile is not null) await DeployAsync(ActiveProfile, askFirst: false);
    }

    [RelayCommand]
    private async Task PlayAsync()
    {
        try
        {
            var state = SafeState();
            var hasPlugins = state?.Mods.Any(m => m.Files.Any(f => f.Kind == "plugin")) ?? false;
            var problems = _app.Game.PreflightProblems(hasPlugins);
            if (problems.Count > 0)
            {
                Notify(problems[0]);
                return;
            }

            if (ActiveProfile is not null && DeployBeforePlay)
            {
                var plan = await _app.Deploy.PlanAsync(ActiveProfile, CancellationToken.None);
                if (plan.HasChanges && !await DeployAsync(ActiveProfile, askFirst: false)) return;
            }

            _app.Game.Launch();
            Notify("Starting the game.");
        }
        catch (Exception ex) when (ex is UserException or HttpRequestException or TaskCanceledException)
        {
            Notify(ex.Message);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            await _app.Catalog.RefreshIndexAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is UserException or HttpRequestException or TaskCanceledException)
        {
            Notify(ex.Message);
        }

        await BrowsePage.ReloadAsync();
        await RefreshStateAsync();
        await CheckUpdatesAsync();
    }

    /// <summary>Applies a new update-check interval right away; the next check is that long from now.</summary>
    public void SetUpdateInterval(int hours)
    {
        _updateTimer.Stop();
        _updateTimer.Interval = TimeSpan.FromHours(Math.Max(1, hours));
        _updateTimer.Start();
    }

    /// <summary>Shows a short message in the status line. It clears itself after a few seconds.</summary>
    public void Notify(string text)
    {
        Message = text;
        _ = ClearMessageLaterAsync(text);
    }

    public Task<bool> ConfirmAsync(string title, string body, string okText) =>
        _app.Confirm(title, body, okText);

    /// <summary>Reloads profiles and the deployed state, computes the pending changes, and tells the other pages.</summary>
    public async Task RefreshStateAsync()
    {
        var profiles = _app.Profiles.List();
        ProfileChoices.Clear();
        foreach (var profile in profiles) ProfileChoices.Add(profile);

        var deployedId = SafeState()?.ProfileId;
        _syncing = true;
        ActiveProfile = profiles.FirstOrDefault(p => p.Id == deployedId) ?? profiles.FirstOrDefault();
        ProfileSelection = ActiveProfile;
        _syncing = false;

        CanDeploy = false;
        PlayNote = string.Empty;
        if (!_app.Game.IsConfigured)
        {
            PendingText = "Choose the game folder in Settings.";
        }
        else if (_app.Deploy.HasPendingRollback)
        {
            CanDeploy = true;
            PendingText = "An earlier deploy is not fully undone. Close the game and press Deploy to finish it.";
        }
        else if (ActiveProfile is null)
        {
            PendingText = "No profile yet. Add a mod from Browse.";
        }
        else if (_app.Catalog.Index is null)
        {
            PendingText = "The catalog is not loaded yet.";
        }
        else
        {
            try
            {
                var plan = await _app.Deploy.PlanAsync(ActiveProfile, CancellationToken.None);
                CanDeploy = plan.HasChanges && plan.Blocked.Count == 0;
                PendingText = plan.Blocked.Count > 0
                    ? plan.Blocked[0]
                    : plan.HasChanges ? $"Pending: {plan.Summary}" : "The game folder matches this profile.";
                if (plan.HasChanges && !DeployBeforePlay) PlayNote = "Play without deploying? Changes are pending.";
            }
            catch (Exception ex) when (ex is UserException or HttpRequestException or TaskCanceledException)
            {
                PendingText = ex.Message;
            }
        }

        // Downloads and profile changes can add or settle cache updates.
        FindCacheUpdates();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Loads the newest index, then looks for newer versions of downloaded mods.</summary>
    public async Task CheckUpdatesAsync()
    {
        try
        {
            await _app.Catalog.RefreshIndexAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is UserException or HttpRequestException or TaskCanceledException)
        {
            AppLog.Info($"Update check skipped: {ex.Message}");
        }

        FindCacheUpdates();
    }

    /// <summary>
    /// Mods in the download cache whose newest version is not downloaded yet. A mod pinned in every profile that uses
    /// it is left out: no profile would use the new version.
    /// </summary>
    private void FindCacheUpdates()
    {
        _cacheUpdates = [];
        if (_app.Catalog.Index is not null)
        {
            var profiles = _app.Profiles.List();
            foreach (var groupId in _app.Downloads.CachedVersionIds().Select(v => v.GroupId).Distinct())
            {
                var group = _app.Catalog.Group(groupId);
                if (group is null || _app.Downloads.IsCached(groupId, group.LatestVersionId)) continue;

                var users = profiles.Where(p => p.GroupIds.Contains(groupId) || p.Pins.ContainsKey(groupId)).ToList();
                if (users.Count > 0 && users.All(p => p.Pins.ContainsKey(groupId))) continue;
                _cacheUpdates.Add(group);
            }
        }

        HasUpdates = _cacheUpdates.Count > 0;
        UpdateBanner = HasUpdates
            ? $"Newer versions of {_cacheUpdates.Count} downloaded mod(s): {string.Join(", ", _cacheUpdates.Take(3).Select(g => g.Name))}" +
              (_cacheUpdates.Count > 3 ? ", …" : string.Empty)
            : string.Empty;
        StatusText = BuildStatusText();
    }

    /// <summary>
    /// Downloads the newest version of each mod in <see cref="FindCacheUpdates"/>, with what it newly depends on, into the
    /// cache. The game folder is not touched: Deploy installs the new versions in the profiles that use them.
    /// </summary>
    [RelayCommand]
    private async Task DownloadUpdatesAsync()
    {
        FindCacheUpdates();
        if (_cacheUpdates.Count == 0)
        {
            Notify("All downloaded mods are up to date.");
            return;
        }

        try
        {
            var lines = new List<string>();
            var docs = new Dictionary<(int, int), F3M.Shared.Models.VersionDocument>();
            foreach (var group in _cacheUpdates)
            {
                var resolved = await _app.Catalog.ResolveAsync([group.GroupId], new Dictionary<int, int>(), CancellationToken.None);
                foreach (var doc in resolved.Values.Where(d => !_app.Downloads.IsCached(d.GroupId, d.VersionId)))
                    docs.TryAdd((doc.GroupId, doc.VersionId), doc);

                var cached = _app.Downloads.CachedVersionIds().Where(v => v.GroupId == group.GroupId).Max(v => v.VersionId);
                var from = await CachedVersionNameAsync(group.GroupId, cached);
                lines.Add($"  **{Markup.Escape(group.Name)}**: {Markup.Escape(from)} to {Markup.Escape(group.LatestVersion)}");
            }

            var extra = docs.Values.Where(d => _cacheUpdates.All(g => g.GroupId != d.GroupId)).Select(d => d.Name).ToList();
            if (extra.Count > 0) lines.Add($"\nNewly needed and downloaded too: {Markup.Escape(string.Join(", ", extra))}");
            lines.Add($"\nAbout {FileOps.FormatBytes(_app.Downloads.BytesToDownload(docs.Values))} to download.");
            lines.Add("Only the download cache changes; the game folder stays as it is. " +
                      "Press Deploy afterwards to install the new versions in a profile that uses them.");

            if (!await ConfirmAsync("Download updates", "New versions:\n" + string.Join("\n", lines), "Download")) return;

            var list = docs.Values.ToList();
            var op = await _app.Ops.RunAsync("Download", "Download updates", _app.Ops.DownloadGate,
                (item, ct) => _app.Downloads.EnsureCachedAsync(list, item, ct));
            Notify(op.Status == "Done"
                ? "Updates downloaded. Press Deploy to install them in the active profile."
                : op.Detail);
        }
        catch (Exception ex) when (ex is UserException or HttpRequestException or TaskCanceledException)
        {
            Notify(ex.Message);
        }

        await RefreshStateAsync();
    }

    /// <summary>The version name of a cached version, from its locally kept catalog document when there is one.</summary>
    private async Task<string> CachedVersionNameAsync(int groupId, int versionId)
    {
        try
        {
            return "v" + (await _app.Catalog.GetVersionAsync(groupId, versionId, CancellationToken.None)).Version;
        }
        catch (Exception ex) when (ex is UserException or HttpRequestException or TaskCanceledException or IOException)
        {
            return "the downloaded version";
        }
    }

    /// <summary>Makes the game folder match a profile. Downloads what is missing, then asks before any destructive step.</summary>
    public async Task<bool> DeployAsync(ProfileDef profile, bool askFirst)
    {
        try
        {
            // Finish undoing an earlier deploy first; its backups hold the original files.
            if (_app.Deploy.HasPendingRollback)
            {
                if (!await Task.Run(_app.Deploy.Rollback))
                {
                    Notify("An earlier deploy still can't be undone: a file in the game folder is in use. Close the game and try again.");
                    await RefreshStateAsync();
                    return false;
                }

                Notify("The earlier deploy was undone.");
                await RefreshStateAsync();
            }

            var plan = await _app.Deploy.PlanAsync(profile, CancellationToken.None);
            if (plan.Blocked.Count > 0)
            {
                Notify(plan.Blocked[0]);
                return false;
            }

            if (!plan.HasChanges && profile.Id == SafeState()?.ProfileId)
            {
                Notify("The game folder already matches this profile.");
                return true;
            }

            if (plan.NeedsDownload.Count > 0)
            {
                var body = $"Profile **{Markup.Escape(profile.Name)}**: {plan.Describe(_app.Game.RootOrThrow())}\nDownloads first, then the deploy starts.";
                if (!await ConfirmAsync(profile.Name, body, "Download and deploy")) return false;

                var download = await _app.Ops.RunAsync("Download", $"Download for {profile.Name}", _app.Ops.DownloadGate,
                    (op, ct) => _app.Downloads.EnsureCachedAsync(plan.NeedsDownload, op, ct));
                if (download.Status != "Done")
                {
                    Notify(download.Detail);
                    return false;
                }

                plan = await _app.Deploy.PlanAsync(profile, CancellationToken.None);
                if (plan.Blocked.Count > 0)
                {
                    Notify(plan.Blocked[0]);
                    return false;
                }
            }
            else if (askFirst)
            {
                if (!await ConfirmAsync($"Switch to {profile.Name}",
                        $"Switch the game folder to **{Markup.Escape(profile.Name)}**: {plan.Describe(_app.Game.RootOrThrow())}", "Switch")) return false;
            }

            if (plan.NeedsConfirmation && !askFirst)
            {
                if (!await ConfirmAsync($"Deploy {profile.Name}",
                        $"Deploy **{Markup.Escape(profile.Name)}**: {plan.Describe(_app.Game.RootOrThrow())}", "Deploy")) return false;
            }

            var result = await _app.Ops.RunAsync("Deploy", $"Deploy {profile.Name}", _app.Ops.DeployGate,
                (op, ct) =>
                {
                    _app.Deploy.Apply(plan, confirmed: true, ct);
                    return Task.CompletedTask;
                });

            Notify(result.Status == "Done" ? $"Deployed {profile.Name}." : result.Detail);
            await RefreshStateAsync();
            return result.Status == "Done";
        }
        catch (Exception ex) when (ex is UserException or HttpRequestException or TaskCanceledException)
        {
            Notify(ex.Message);
            return false;
        }
    }

    /// <summary>Downloads a mod version and its dependencies into the cache. The game folder is not touched.</summary>
    public async Task DownloadModAsync(int groupId, int? versionId, string title)
    {
        try
        {
            var pins = versionId is null
                ? new Dictionary<int, int>()
                : new Dictionary<int, int> { [groupId] = versionId.Value };
            var docs = await _app.Catalog.ResolveAsync(new[] { groupId }, pins, CancellationToken.None);
            var list = docs.Values.ToList();
            var op = await _app.Ops.RunAsync("Download", title, _app.Ops.DownloadGate,
                (item, ct) => _app.Downloads.EnsureCachedAsync(list, item, ct));
            Notify(op.Status == "Done" ? $"Cached {title}." : op.Detail);
            await RefreshStateAsync();
        }
        catch (Exception ex) when (ex is UserException or HttpRequestException or TaskCanceledException)
        {
            Notify(ex.Message);
        }
    }

    public async Task AddToActiveProfileAsync(int groupId, string name)
    {
        var profile = ActiveProfile ?? _app.Profiles.Create("My setup");
        if (!profile.GroupIds.Contains(groupId)) profile.GroupIds.Add(groupId);
        _app.Profiles.Save(profile);
        Notify($"Added {name} to {profile.Name}. Deploy to apply it.");
        await RefreshStateAsync();
    }

    /// <summary>Entry point for a second launch and for f3m:// links. Every link is confirmed before it acts.</summary>
    public async Task HandleMessageAsync(string message)
    {
        if (message == "show")
        {
            ShowRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!RateAllows(message))
        {
            Notify("Too many links in a short time. Ignored.");
            return;
        }

        if (!ProtocolLinks.TryParse(message, out var request, out var error) || request is null)
        {
            Notify(error ?? "That link is not valid.");
            return;
        }

        ShowRequested?.Invoke(this, EventArgs.Empty);
        switch (request)
        {
            case OpenModRequest open:
                CurrentPage = BrowsePage;
                await BrowsePage.SelectGroupAsync(open.GroupId);
                break;

            case DownloadModRequest download:
                await ConfirmDownloadAsync(download);
                break;

            case ImportProfileRequest import:
                await ConfirmImportAsync(import);
                break;
        }
    }

    /// <summary>At most five links in a minute, and an identical link is ignored within ten seconds.</summary>
    private bool RateAllows(string message)
    {
        var now = DateTimeOffset.UtcNow;
        if (message == _lastLink && now - _lastLinkAt < TimeSpan.FromSeconds(10)) return false;

        while (_linkTimes.Count > 0 && now - _linkTimes.Peek() > TimeSpan.FromMinutes(1)) _linkTimes.Dequeue();
        if (_linkTimes.Count >= 5) return false;

        _linkTimes.Enqueue(now);
        _lastLink = message;
        _lastLinkAt = now;
        return true;
    }

    private async Task ConfirmDownloadAsync(DownloadModRequest request)
    {
        try
        {
            var group = _app.Catalog.Group(request.GroupId)
                        ?? throw new UserException($"Mod {request.GroupId} is not in the catalog.");
            var versionId = request.VersionId ?? group.LatestVersionId;
            var doc = await _app.Catalog.GetVersionAsync(request.GroupId, versionId, CancellationToken.None);
            var size = doc.Files.Sum(f => f.Size);
            var body = $"**{Markup.Escape(doc.Name)}** {doc.Version} by {Markup.Escape(doc.Author)}\nSize: {FileOps.FormatBytes(size)}\n\n" +
                       "Requested from your browser. The app cannot tell which site sent this request.";
            if (!await ConfirmAsync("Download mod", body, "Download")) return;
            await DownloadModAsync(request.GroupId, versionId, doc.Name);
        }
        catch (Exception ex) when (ex is UserException or HttpRequestException or TaskCanceledException)
        {
            Notify(ex.Message);
        }
    }

    private async Task ConfirmImportAsync(ImportProfileRequest request)
    {
        try
        {
            var summary = await _app.Profiles.PreviewImportAsync(request.Collection, CancellationToken.None);
            var lines = new List<string> { $"Mods ({summary.Mods.Count}):" };
            lines.AddRange(summary.Mods.Select(m => $"  **{Markup.Escape(m.Name)}** by {Markup.Escape(m.Author)}"));
            if (summary.AddedDependencies.Count > 0)
                lines.Add($"Dependencies added automatically: {string.Join(", ", summary.AddedDependencies)}");
            if (summary.Unavailable.Count > 0)
                lines.Add($"Not in the catalog, left out: {string.Join(", ", summary.Unavailable)}");
            lines.Add($"About {FileOps.FormatBytes(summary.DownloadBytes)} to download later.");
            lines.Add("Nothing is downloaded or deployed by adding this profile.");

            var name = request.Collection.Name ?? "Imported collection";
            if (!await ConfirmAsync($"Add profile \"{name}\"?", string.Join("\n", lines), "Add profile")) return;

            var profile = _app.Profiles.Create(name, summary.Available);
            await RefreshStateAsync();
            CurrentPage = ProfilesPage;
            Notify($"Added {profile.Name}. Switch to it when you are ready.");
        }
        catch (Exception ex) when (ex is UserException or HttpRequestException or TaskCanceledException)
        {
            Notify(ex.Message);
        }
    }

    private async Task SwitchToAsync(ProfileDef profile)
    {
        var switched = await DeployAsync(profile, askFirst: true);
        if (switched) return;

        _syncing = true;
        ProfileSelection = ActiveProfile;
        _syncing = false;
    }

    public string BuildStatusText()
    {
        var running = _app.Ops.Items.Count(i => !i.IsFinished);
        var parts = new List<string>
        {
            running == 1 ? "1 operation" : $"{running} operations",
            GameRunning ? "game running" : "game not running",
        };
        if (UpdateBanner.Length > 0) parts.Add(UpdateBanner);
        return string.Join("  \u00b7  ", parts);
    }

    private DeployedState? SafeState()
    {
        try
        {
            return _app.Deploy.LoadState();
        }
        catch (UserException)
        {
            return null;
        }
    }

    private void SaveSettingsSafe()
    {
        try
        {
            _app.SaveSettings();
        }
        catch (IOException ex)
        {
            AppLog.Error("Settings could not be saved", ex);
        }
    }

    private async Task ClearMessageLaterAsync(string text)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (Message == text) Message = string.Empty;
    }
}
