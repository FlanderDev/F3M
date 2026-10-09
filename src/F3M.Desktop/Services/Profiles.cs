using System.Text.Json;
using F3M.Desktop.Core;
using F3M.Shared.Helpers;
using F3M.Shared.Models;

namespace F3M.Desktop.Services;

/// <summary>What importing a collection would create. Nothing is downloaded or deployed by building this.</summary>
public sealed record ImportSummary(
    IReadOnlyList<int> Available,
    IReadOnlyList<IndexGroup> Mods,
    IReadOnlyList<string> AddedDependencies,
    IReadOnlyList<int> Unavailable,
    long DownloadBytes);

/// <summary>Local profiles, one folder per game (plan 6.9, 10). Profiles never leave the machine except as share links.</summary>
public sealed class Profiles(AppServices app)
{
    private string Folder => Path.Combine(app.Paths.Profiles, app.GameId);

    /// <summary>How many profiles exist for a game folder, without switching to it.</summary>
    public int CountFor(string gameFolder)
    {
        var folder = Path.Combine(app.Paths.Profiles, AppServices.GameIdFor(gameFolder));
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.json").Count() : 0;
    }

    public List<ProfileDef> List()
    {
        var result = new List<ProfileDef>();
        if (!Directory.Exists(Folder)) return result;

        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                var profile = FileOps.ReadJson<ProfileDef>(file);
                if (profile is not null) result.Add(profile);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                AppLog.Error($"Skipping unreadable profile {file}", ex);
            }
        }

        return result.OrderBy(p => p.CreatedAt).ToList();
    }

    public ProfileDef Create(string name, IEnumerable<int>? groupIds = null)
    {
        var profile = new ProfileDef
        {
            Name = UniqueName(name),
            GroupIds = (groupIds ?? []).Distinct().ToList(),
        };
        Save(profile);
        return profile;
    }

    public ProfileDef Duplicate(ProfileDef source)
    {
        var copy = Create(source.Name + " copy", source.GroupIds);
        copy.Pins = new Dictionary<int, int>(source.Pins);
        Save(copy);
        return copy;
    }

    /// <summary>Builds a profile from what is deployed right now. Pins are not carried over.</summary>
    public ProfileDef CaptureDeployed()
    {
        var state = app.Deploy.LoadState();
        return Create("Captured setup", state.Mods.Select(m => m.GroupId));
    }

    public void Save(ProfileDef profile) => FileOps.WriteJsonAtomic(Path.Combine(Folder, profile.Id + ".json"), profile);

    public void Delete(ProfileDef profile)
    {
        var file = Path.Combine(Folder, profile.Id + ".json");
        if (File.Exists(file)) File.Delete(file);
    }

    public string UniqueName(string name)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "Profile" : name.Trim();
        var taken = List().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName)) return baseName;

        for (var n = 2; ; n++)
        {
            var candidate = $"{baseName} {n}";
            if (!taken.Contains(candidate)) return candidate;
        }
    }

    /// <summary>
    /// The groups that would stop working if <paramref name="groupId"/> were removed: every resolved mod that depends on
    /// it, directly or through another dependent. Used by the dependency guard (plan 6.6).
    /// </summary>
    public async Task<List<int>> DependentIdsAsync(ProfileDef profile, int groupId, CancellationToken ct)
    {
        var resolved = await app.Catalog.ResolveAsync(profile.GroupIds, profile.Pins, ct);
        var dependents = new HashSet<int>();
        var frontier = new Queue<int>(new[] { groupId });
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var (id, document) in resolved)
            {
                if (id == groupId || dependents.Contains(id)) continue;
                if (document.Dependencies.Any(d => d.GroupId == current))
                {
                    dependents.Add(id);
                    frontier.Enqueue(id);
                }
            }
        }

        return dependents.OrderBy(id => id).ToList();
    }

    /// <summary>The link that shares this profile on the website. Pins are left out (plan 10.2).</summary>
    public string WebLink(ProfileDef profile)
    {
        var ids = string.Join(',', profile.GroupIds.Distinct());
        if (!CollectionLink.TryParse(ids, profile.Name, out var link, out var error) || link is null)
            throw new UserException(error ?? "This profile cannot be shared.");
        return app.Settings.ServerUrl.TrimEnd('/') + link.ToWebPath();
    }

    /// <summary>Resolves a collection for the import dialog. Unknown IDs are listed as unavailable and left out.</summary>
    public async Task<ImportSummary> PreviewImportAsync(CollectionLink link, CancellationToken ct)
    {
        await app.Catalog.EnsureIndexAsync(ct);
        var available = link.GroupIds.Where(id => app.Catalog.Group(id) is not null).ToList();
        var unavailable = link.GroupIds.Where(id => app.Catalog.Group(id) is null).ToList();
        var resolved = available.Count == 0
            ? new Dictionary<int, F3M.Shared.Models.VersionDocument>()
            : await app.Catalog.ResolveAsync(available, new Dictionary<int, int>(), ct);

        var mods = available.Select(id => app.Catalog.Group(id)!).ToList();
        var added = resolved.Keys.Where(id => !available.Contains(id))
            .Select(id => resolved[id].Name).OrderBy(n => n).ToList();
        var bytes = resolved.Values.Sum(d => d.Files.Sum(f => f.Size));
        return new ImportSummary(available, mods, added, unavailable, bytes);
    }
}
