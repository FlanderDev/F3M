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
    private List<string> _updateNames = [];

    public MainViewModel(AppServices app)
    {
        _app = app;
        BrowsePage = new BrowseViewModel(app, this);
        LibraryPage = new LibraryViewModel(app, this);
        ProfilesPage = new ProfilesViewModel(app, this);
        DownloadsPage = new DownloadsViewModel(app);
        SettingsPage = new SettingsViewModel(app, this);
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
    private void ShowBrowse() => CurrentPage = BrowsePage;

    [RelayCommand]
    private void ShowLibrary() => CurrentPage = LibraryPage;

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

        StatusText = BuildStatusText();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Checks the catalog for newer versions of deployed mods. The banner offers to apply them.</summary>
    public async Task CheckUpdatesAsync()
    {
        UpdateBanner = string.Empty;
        _updateNames = [];
        if (!_app.Game.IsConfigured || ActiveProfile is null) return;

        try
        {
            await _app.Catalog.RefreshIndexAsync(CancellationToken.None);
            var pinned = ActiveProfile.Pins.Keys.ToHashSet();
            foreach (var mod in SafeState()?.Mods ?? Enumerable.Empty<DeployedMod>())
            {
                var group = _app.Catalog.Group(mod.GroupId);
                if (group is not null && group.LatestVersionId != mod.VersionId && !pinned.Contains(mod.GroupId))
                    _updateNames.Add(group.Name);
            }

            if (_updateNames.Count > 0)
                UpdateBanner = $"{_updateNames.Count} update(s) available: {string.Join(", ", _updateNames.Take(3))}";
        }
        catch (Exception ex) when (ex is UserException or HttpRequestException or TaskCanceledException)
        {
            AppLog.Info($"Update check skipped: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ApplyUpdatesAsync()
    {
        if (ActiveProfile is null) return;

        // The changelog: each new version's description, since the document has no separate changelog field yet.
        // Pinned mods keep their version on deploy, so they are not listed as updates (same rule as the banner).
        var notes = new List<string>();
        var pinned = ActiveProfile.Pins.Keys.ToHashSet();
        try
        {
            foreach (var mod in SafeState()?.Mods ?? Enumerable.Empty<DeployedMod>())
            {
                var group = _app.Catalog.Group(mod.GroupId);
                if (group is null || group.LatestVersionId == mod.VersionId || pinned.Contains(mod.GroupId)) continue;
                var doc = await _app.Catalog.GetVersionAsync(mod.GroupId, group.LatestVersionId, CancellationToken.None);
                notes.Add($"**{doc.Name}** {mod.Version} to {doc.Version}\n{doc.Description}");
            }
        }
        catch (Exception ex) when (ex is UserException or HttpRequestException or TaskCanceledException)
        {
            Notify(ex.Message);
            return;
        }

        if (!await ConfirmAsync("Apply updates", string.Join("\n\n", notes), "Update and deploy")) return;
        await DeployAsync(ActiveProfile, askFirst: false);
        await CheckUpdatesAsync();
    }

    /// <summary>Makes the game folder match a profile. Downloads what is missing, then asks before any destructive step.</summary>
    public async Task<bool> DeployAsync(ProfileDef profile, bool askFirst)
    {
        try
        {
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
                var body = $"Profile **{profile.Name}**: {plan.Describe(_app.Game.RootOrThrow())}\nDownloads first, then the deploy starts.";
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
                        $"Switch the game folder to **{profile.Name}**: {plan.Describe(_app.Game.RootOrThrow())}", "Switch")) return false;
            }

            if (plan.NeedsConfirmation && !askFirst)
            {
                if (!await ConfirmAsync($"Deploy {profile.Name}",
                        $"Deploy **{profile.Name}**: {plan.Describe(_app.Game.RootOrThrow())}", "Deploy")) return false;
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
            var body = $"**{doc.Name}** {doc.Version} by {doc.Author}\nSize: {FileOps.FormatBytes(size)}\n\n" +
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
            lines.AddRange(summary.Mods.Select(m => $"  **{m.Name}** by {m.Author}"));
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
