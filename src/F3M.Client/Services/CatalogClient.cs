using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using F3M.Shared.Models;

namespace F3M.Client.Services;

/// <summary>One group in a collection. Null parts mean the catalog does not have it (unavailable).</summary>
public sealed record CatalogEntry(int GroupId, IndexGroup? Group, VersionDocument? Version)
{
    public bool IsAvailable => Group is not null && Version is not null;

    public long TotalBytes => Version?.Files.Sum(f => f.Size) ?? 0;
}

/// <summary>
/// A collection resolved against the catalog. <see cref="Dependencies"/> holds the groups the desktop app
/// would add automatically (transitively), not listed in the collection itself.
/// </summary>
public sealed record CollectionView(IReadOnlyList<CatalogEntry> Mods, IReadOnlyList<CatalogEntry> Dependencies)
{
    public long TotalBytes => Mods.Sum(m => m.TotalBytes) + Dependencies.Sum(d => d.TotalBytes);
}

/// <summary>
/// Reads the static catalog published under /catalog (plan 5). The website only displays it; signatures are
/// checked by the desktop app, which is the component that installs files.
/// </summary>
public sealed class CatalogClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Returns null when nothing has been published yet.</summary>
    public async Task<IndexDocument?> GetIndexAsync(CancellationToken ct = default)
    {
        var envelope = await GetEnvelopeAsync("catalog/index.json", ct);
        return envelope is null ? null : JsonSerializer.Deserialize<IndexDocument>(envelope.Document, Json);
    }

    public async Task<VersionDocument?> GetVersionAsync(int groupId, int versionId, CancellationToken ct = default)
    {
        var envelope = await GetEnvelopeAsync($"catalog/mods/{groupId}/{versionId}.json", ct);
        return envelope is null ? null : JsonSerializer.Deserialize<VersionDocument>(envelope.Document, Json);
    }

    /// <summary>
    /// Resolves the collection against the index and loads the latest version of every mod and,
    /// transitively, of every dependency. Unknown IDs come back as unavailable entries.
    /// </summary>
    public async Task<CollectionView> LoadCollectionAsync(IReadOnlyList<int> groupIds, CancellationToken ct = default)
    {
        var index = await GetIndexAsync(ct);
        var groups = index?.Groups.ToDictionary(g => g.GroupId) ?? new Dictionary<int, IndexGroup>();

        var versions = new Dictionary<int, VersionDocument?>();
        var queue = new Queue<int>(groupIds.Distinct());
        var visited = new HashSet<int>(queue);
        var dependencyIds = new List<int>();
        var modIds = groupIds.Distinct().ToHashSet();

        while (queue.Count > 0)
        {
            var batch = new List<int>();
            while (queue.Count > 0) batch.Add(queue.Dequeue());

            var loaded = await Task.WhenAll(batch.Select(id => LoadLatestAsync(groups, id, ct)));
            for (var i = 0; i < batch.Count; i++)
            {
                var id = batch[i];
                versions[id] = loaded[i];
                if (loaded[i] is null) continue;

                foreach (var dep in loaded[i]!.Dependencies)
                {
                    if (!visited.Add(dep.GroupId)) continue;
                    queue.Enqueue(dep.GroupId);
                    dependencyIds.Add(dep.GroupId);
                }
            }
        }

        CatalogEntry Entry(int id) =>
            new(id, groups.GetValueOrDefault(id), versions.GetValueOrDefault(id));

        return new CollectionView(
            groupIds.Distinct().Select(Entry).ToList(),
            dependencyIds.Where(id => !modIds.Contains(id)).Select(Entry).ToList());
    }

    private async Task<VersionDocument?> LoadLatestAsync(
        IReadOnlyDictionary<int, IndexGroup> groups, int groupId, CancellationToken ct)
    {
        // Collections always follow the latest approved version (plan 10.4); "latest" is the only form used.
        if (!groups.TryGetValue(groupId, out var group)) return null;
        return await GetVersionAsync(groupId, group.LatestVersionId, ct);
    }

    private async Task<SignedEnvelope?> GetEnvelopeAsync(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SignedEnvelope>(Json, ct);
    }
}
