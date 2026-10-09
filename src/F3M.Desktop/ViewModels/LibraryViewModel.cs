using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using F3M.Desktop.Core;
using F3M.Desktop.Services;

namespace F3M.Desktop.ViewModels;

/// <summary>One mod of the active profile, with the actions available for it.</summary>
public sealed partial class LibraryRow : ObservableObject
{
    public LibraryRow(
        LibraryViewModel owner,
        int groupId,
        int versionId,
        string name,
        string version,
        string state,
        bool isDependency,
        string requiredBy,
        bool hasConfig,
        bool isDeployed)
    {
        GroupId = groupId;
        VersionId = versionId;
        Name = name;
        Version = version;
        State = state;
        IsDependency = isDependency;
        RequiredBy = requiredBy;
        HasConfig = hasConfig;
        IsDeployed = isDeployed;

        RemoveCommand = new AsyncRelayCommand(() => owner.RemoveAsync(this));
        PinCommand = new RelayCommand(() => owner.Pin(this));
        UnpinCommand = new RelayCommand(() => owner.Unpin(this));
        ClearGeneratedCommand = new AsyncRelayCommand(() => owner.ClearGeneratedAsync(this));
        ConfigCommand = new AsyncRelayCommand(() => owner.OpenConfigAsync(this));
    }

    public int GroupId { get; }

    /// <summary>The version the profile resolves to now, which is the pinned one when a pin exists.</summary>
    public int VersionId { get; }
    public string Name { get; }
    public string Version { get; }
    public string State { get; }
    public bool IsDependency { get; }
    public string RequiredBy { get; }
    public bool HasConfig { get; }
    public bool IsDeployed { get; }

    public IAsyncRelayCommand RemoveCommand { get; }
    public IRelayCommand PinCommand { get; }
    public IRelayCommand UnpinCommand { get; }
    public IAsyncRelayCommand ClearGeneratedCommand { get; }
    public IAsyncRelayCommand ConfigCommand { get; }
}

/// <summary>The mods of the active profile, their deployed state, and the per-mod actions (plan 4.1, 6.6, 5.8).</summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly AppServices _app;
    private readonly MainViewModel _shell;

    public LibraryViewModel(AppServices app, MainViewModel shell)
    {
        _app = app;
        _shell = shell;
        _shell.StateChanged += (_, _) => _ = RefreshAsync();
    }

    public ObservableCollection<LibraryRow> Rows { get; } = [];

    /// <summary>Bumped by every refresh. A refresh whose number is no longer current drops its result.</summary>
    private int _refreshGeneration;

    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>
    /// Rebuilds the list. Refreshes can overlap (every state change starts one), so the rows are built first and only
    /// the newest refresh puts them on screen.
    /// </summary>
    public async Task RefreshAsync()
    {
        var generation = ++_refreshGeneration;
        var profile = _shell.ActiveProfile;
        if (profile is null)
        {
            Rows.Clear();
            Summary = "No profile yet. Browse the catalog and add a mod.";
            return;
        }

        var rows = new List<LibraryRow>();
        string summary;
        try
        {
            var resolved = await _app.Catalog.ResolveAsync(profile.GroupIds, profile.Pins, CancellationToken.None);
            if (generation != _refreshGeneration) return;
            var deployed = _app.Deploy.LoadState().Mods.ToDictionary(m => m.GroupId);

            foreach (var (groupId, doc) in resolved.OrderBy(r => r.Value.Name, StringComparer.OrdinalIgnoreCase))
            {
                var isDependency = !profile.GroupIds.Contains(groupId);
                var requiredBy = string.Join(", ", resolved.Values
                    .Where(d => d.Dependencies.Any(x => x.GroupId == groupId))
                    .Select(d => d.Name));

                deployed.TryGetValue(groupId, out var mod);
                var state = mod is null
                    ? (_app.Downloads.IsCached(groupId, doc.VersionId) ? "Cached, not deployed" : "Not downloaded")
                    : mod.VersionId == doc.VersionId
                        ? $"Deployed v{mod.Version}"
                        : $"Deployed v{mod.Version}, update to v{doc.Version}";

                rows.Add(new LibraryRow(
                    this, groupId, doc.VersionId, doc.Name, doc.Version, state,
                    isDependency, requiredBy,
                    mod?.Files.Any(f => f.Kind == "config") ?? false,
                    mod is not null));
            }

            var direct = profile.GroupIds.Count;
            var dependencies = resolved.Count - profile.GroupIds.Count(id => resolved.ContainsKey(id));
            summary = $"{profile.Name}: {direct} mod(s), {dependencies} dependenc{(dependencies == 1 ? "y" : "ies")}";
        }
        catch (Exception ex) when (ex is UserException or HttpRequestException or TaskCanceledException)
        {
            summary = ex.Message;
        }

        if (generation != _refreshGeneration) return;
        Rows.Clear();
        foreach (var row in rows) Rows.Add(row);
        Summary = summary;
    }

    /// <summary>Removes a mod from the profile. Dependents block the removal unless the user also removes them.</summary>
    public async Task RemoveAsync(LibraryRow row)
    {
        var profile = _shell.ActiveProfile;
        if (profile is null) return;
        if (row.IsDependency)
        {
            _shell.Notify($"{row.Name} is a dependency of {row.RequiredBy}. Remove that mod instead.");
            return;
        }

        try
        {
            var toRemove = new List<int> { row.GroupId };
            var dependents = await _app.Profiles.DependentIdsAsync(profile, row.GroupId, CancellationToken.None);
            if (dependents.Count > 0)
            {
                var names = string.Join(", ", dependents.Select(id => $"**{Markup.Escape(_app.Catalog.Group(id)?.Name ?? id.ToString())}**"));
                var ok = await _shell.ConfirmAsync(
                    "Other mods need this one",
                    $"**{Markup.Escape(row.Name)}** is required by {names}. Without it they will not load.\n\nDisable them as well?",
                    "Also disable dependents");
                if (!ok) return;
                toRemove.AddRange(dependents);
            }

            foreach (var id in toRemove) profile.GroupIds.Remove(id);
            _app.Profiles.Save(profile);
            _shell.Notify($"Removed {row.Name}. Deploy to apply it.");
            await _shell.RefreshStateAsync();
        }
        catch (Exception ex) when (ex is UserException or HttpRequestException or TaskCanceledException)
        {
            _shell.Notify(ex.Message);
        }
    }

    /// <summary>Freezes the mod at the version the profile resolves to now. Local to this machine.</summary>
    public void Pin(LibraryRow row)
    {
        var profile = _shell.ActiveProfile;
        if (profile is null) return;
        profile.Pins[row.GroupId] = row.VersionId;
        _app.Profiles.Save(profile);
        _shell.Notify($"{row.Name} is pinned to v{row.Version}.");
        _ = _shell.RefreshStateAsync();
    }

    public void Unpin(LibraryRow row)
    {
        var profile = _shell.ActiveProfile;
        if (profile is null) return;
        profile.Pins.Remove(row.GroupId);
        _app.Profiles.Save(profile);
        _shell.Notify($"{row.Name} follows the latest version again.");
        _ = _shell.RefreshStateAsync();
    }

    /// <summary>Runs the generated-file scan for one deployed mod without removing the mod (plan 5.8).</summary>
    public async Task ClearGeneratedAsync(LibraryRow row)
    {
        try
        {
            var mod = _app.Deploy.LoadState().Mods.FirstOrDefault(m => m.GroupId == row.GroupId);
            if (mod is null)
            {
                _shell.Notify($"{row.Name} is not deployed, so there is nothing to clear.");
                return;
            }

            var root = _app.Game.RootOrThrow();
            var files = _app.Deploy.GeneratedFilesFor(mod);
            if (files.Count == 0)
            {
                _shell.Notify("No generated files were found for this mod.");
                return;
            }

            var list = string.Join("\n", files.Take(30).Select(f => "  " + Markup.Link(f, FileOps.ToRelative(root, f))));
            if (!await _shell.ConfirmAsync("Clear generated files",
                    $"These files match the generated-file patterns of **{Markup.Escape(row.Name)}**:\n{list}", "Remove files"))
                return;

            var removed = 0;
            foreach (var file in files)
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (IOException ex)
                {
                    AppLog.Error($"Could not remove {file}", ex);
                }
            }

            _shell.Notify($"Removed {removed} generated file(s).");
        }
        catch (UserException ex)
        {
            _shell.Notify(ex.Message);
        }
    }

    public async Task OpenConfigAsync(LibraryRow row)
    {
        await _app.OpenConfig(row.GroupId);
    }
}
