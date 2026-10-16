using F3M.Server.Data;
using F3M.Server.Models;
using F3M.Shared.Api;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace F3M.Server.Services;

/// <summary>
/// The tags and their tree, kept in memory: the browse page filters by them on every request. Rebuilt from the
/// database after each change (<see cref="Invalidate"/>). Changes take <see cref="WriteLock"/>, so two admins can't
/// together make a loop that neither change would make alone.
/// </summary>
public sealed class TagIndex
{
    public SemaphoreSlim WriteLock { get; } = new(1, 1);

    private volatile TagSnapshot? _current;

    public async Task<TagSnapshot> GetAsync(AppDbContext db, CancellationToken ct = default) =>
        _current ?? (_current = await LoadAsync(db, ct));

    public void Invalidate() => _current = null;

    /// <summary>Reads the tags straight from the database, not from the copy in memory.</summary>
    public static async Task<TagSnapshot> LoadAsync(AppDbContext db, CancellationToken ct = default)
    {
        var tags = await db.Tags.AsNoTracking().ToListAsync(ct);
        var inclusions = await db.TagInclusions.AsNoTracking().Select(i => new { i.ParentId, i.ChildId }).ToListAsync(ct);
        return new TagSnapshot(tags, new TagGraph(tags.Select(t => t.Id), inclusions.Select(i => (i.ParentId, i.ChildId))));
    }
}

public sealed class TagSnapshot(IReadOnlyList<Tag> tags, TagGraph graph)
{
    public IReadOnlyList<Tag> Tags { get; } = tags;
    public TagGraph Graph { get; } = graph;
    public IReadOnlyDictionary<int, Tag> ById { get; } = tags.ToDictionary(t => t.Id);
    private readonly Dictionary<string, Tag> _byName = tags.ToDictionary(t => t.NormalizedName);

    public Tag? Find(string name) => _byName.GetValueOrDefault(TagRules.Normalize(name));

    public string NameOf(int id) => ById.TryGetValue(id, out var tag) ? tag.Name : $"#{id}";
}

/// <summary>
/// Server side of <see cref="ITagsApi"/>, and the tag work the mod pages need (filters, a mod's tags). Every change is
/// checked against the whole tree (see <see cref="TagGraph.Problem"/>), not only against the tags it touches.
/// </summary>
public sealed class TagService(AppDbContext db, TagIndex index, CatalogService catalog, ILogger<TagService> logger) : ITagsApi
{
    // ── Reading ─────────────────────────────────────────────────────────────

    public async Task<List<TagDto>> GetTags(CancellationToken ct = default)
    {
        var snapshot = await index.GetAsync(db, ct);

        // Only mods the site shows count: groups with an approved latest version.
        var visibleGroups = db.Mods.Where(m => m.IsApproved && m.IsLatestVersion).Select(m => m.ModGroupId);
        var links = await db.ModGroupTags.AsNoTracking()
            .Where(t => visibleGroups.Contains(t.ModGroupId))
            .Select(t => new { t.ModGroupId, t.TagId })
            .ToListAsync(ct);
        var groupsByTag = links.GroupBy(l => l.TagId).ToDictionary(g => g.Key, g => g.Select(l => l.ModGroupId).ToHashSet());

        return [.. snapshot.Tags
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new TagDto
            {
                Id = t.Id,
                Name = t.Name,
                Description = t.Description,
                ParentIds = [.. snapshot.Graph.ParentsOf(t.Id).Order()],
                ModCount = groupsByTag.TryGetValue(t.Id, out var own) ? own.Count : 0,
                TotalModCount = snapshot.Graph.SelfAndDescendants(t.Id)
                    .SelectMany(id => groupsByTag.GetValueOrDefault(id) ?? [])
                    .Distinct()
                    .Count()
            })];
    }

    private async Task<TagDto> GetTagAsync(int id, CancellationToken ct) =>
        (await GetTags(ct)).First(t => t.Id == id);

    // ── Changes (admins) ────────────────────────────────────────────────────

    public async Task<TagDto> Create(TagEditDto dto, CancellationToken ct = default)
    {
        int id;
        await index.WriteLock.WaitAsync(ct);
        try
        {
            var snapshot = await TagIndex.LoadAsync(db, ct);
            var (name, description) = CheckFields(dto, snapshot, null);

            // The new tag has no id yet; 0 stands for it while the tree is checked.
            CheckTree(snapshot, 0, name, dto);

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var tag = new Tag { Name = name, NormalizedName = TagRules.Normalize(name), Description = description };
            db.Tags.Add(tag);
            await db.SaveChangesAsync(ct);
            AddInclusions(tag.Id, dto);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            id = tag.Id;
            index.Invalidate();
            logger.LogInformation("Tag {TagId} ({Name}) was created.", id, name);
        }
        finally
        {
            index.WriteLock.Release();
        }

        await catalog.TryRefreshIndexAsync(ct);
        return await GetTagAsync(id, ct);
    }

    public async Task<TagDto> Update(int id, TagEditDto dto, CancellationToken ct = default)
    {
        await index.WriteLock.WaitAsync(ct);
        try
        {
            var snapshot = await TagIndex.LoadAsync(db, ct);
            var tag = await db.Tags.FindAsync([id], ct) ?? throw new KeyNotFoundException($"No tag with id {id} was found.");
            var (name, description) = CheckFields(dto, snapshot, id);
            CheckTree(snapshot, id, name, dto);

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var oldName = tag.Name;
            tag.Name = name;
            tag.NormalizedName = TagRules.Normalize(name);
            tag.Description = description;
            db.TagInclusions.RemoveRange(db.TagInclusions.Where(i => i.ParentId == id || i.ChildId == id));
            await db.SaveChangesAsync(ct);
            AddInclusions(id, dto);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            index.Invalidate();
            if (oldName != name)
                logger.LogInformation("Tag {TagId} was renamed from {OldName} to {Name}.", id, oldName, name);
            else
                logger.LogInformation("Tag {TagId} ({Name}) was changed.", id, name);
        }
        finally
        {
            index.WriteLock.Release();
        }

        await catalog.TryRefreshIndexAsync(ct);
        return await GetTagAsync(id, ct);
    }

    public async Task Delete(int id, CancellationToken ct = default)
    {
        await index.WriteLock.WaitAsync(ct);
        try
        {
            var tag = await db.Tags.FindAsync([id], ct) ?? throw new KeyNotFoundException($"No tag with id {id} was found.");
            var mods = await db.ModGroupTags.CountAsync(t => t.TagId == id, ct);

            // Its inclusions and its place on mods go with it (cascade).
            db.Tags.Remove(tag);
            await db.SaveChangesAsync(ct);

            index.Invalidate();
            logger.LogInformation("Tag {TagId} ({Name}) was deleted; {Count} mods had it.", id, tag.Name, mods);
        }
        finally
        {
            index.WriteLock.Release();
        }

        await catalog.TryRefreshIndexAsync(ct);
    }

    public async Task<TagDto> Merge(int id, int targetId, CancellationToken ct = default)
    {
        if (id == targetId)
            throw new InvalidDataException("A tag can't be merged into itself.");

        await index.WriteLock.WaitAsync(ct);
        try
        {
            var snapshot = await TagIndex.LoadAsync(db, ct);
            var source = snapshot.ById.GetValueOrDefault(id) ?? throw new KeyNotFoundException($"No tag with id {id} was found.");
            var target = snapshot.ById.GetValueOrDefault(targetId) ?? throw new KeyNotFoundException($"No tag with id {targetId} was found.");

            // The tree without the source, then its inclusions moved to the target one by one. One that would make a
            // loop is left out: the two tags are already related the other way round, so nothing is lost.
            var graph = snapshot.Graph;
            var edges = graph.Edges().Where(e => e.ParentId != id && e.ChildId != id).ToList();
            var moved = new TagGraph(graph.TagIds.Where(t => t != id), edges);
            foreach (var parent in graph.ParentsOf(id).Where(p => p != targetId))
            {
                if (!moved.SelfAndDescendants(targetId).Contains(parent))
                    moved = new TagGraph(moved.TagIds, moved.Edges().Append((parent, targetId)));
            }
            foreach (var child in graph.ChildrenOf(id).Where(c => c != targetId))
            {
                if (!moved.SelfAndAncestors(targetId).Contains(child))
                    moved = new TagGraph(moved.TagIds, moved.Edges().Append((targetId, child)));
            }

            if (moved.Problem(snapshot.NameOf) is { } problem)
                throw new InvalidDataException(problem);

            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // Mods: the target instead of the source, once.
            var groups = await db.ModGroupTags.Where(t => t.TagId == id).Select(t => t.ModGroupId).ToListAsync(ct);
            var haveTarget = await db.ModGroupTags.Where(t => t.TagId == targetId && groups.Contains(t.ModGroupId))
                .Select(t => t.ModGroupId).ToListAsync(ct);
            db.ModGroupTags.AddRange(groups.Except(haveTarget).Select(g => new ModGroupTag { ModGroupId = g, TagId = targetId }));

            // The source with its inclusions and mod links (cascade), then the target's new inclusions.
            db.Tags.Remove(await db.Tags.FindAsync([id], ct) ?? throw new KeyNotFoundException($"No tag with id {id} was found."));
            await db.SaveChangesAsync(ct);

            var existing = edges.ToHashSet();
            db.TagInclusions.AddRange(moved.Edges().Where(e => !existing.Contains(e))
                .Select(e => new TagInclusion { ParentId = e.ParentId, ChildId = e.ChildId }));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            index.Invalidate();
            logger.LogInformation("Tag {TagId} ({Name}) was merged into tag {TargetId} ({TargetName}); {Count} mods had it.",
                id, source.Name, targetId, target.Name, groups.Count);
        }
        finally
        {
            index.WriteLock.Release();
        }

        await catalog.TryRefreshIndexAsync(ct);
        return await GetTagAsync(targetId, ct);
    }

    /// <summary>The cleaned name and description, or why they can't be used.</summary>
    private static (string Name, string? Description) CheckFields(TagEditDto dto, TagSnapshot snapshot, int? id)
    {
        var name = TagRules.Clean(dto.Name);
        if (TagRules.NameProblem(name) is { } problem)
            throw new InvalidDataException(problem);
        if (snapshot.Find(name) is { } other && other.Id != id)
            throw new InvalidDataException($"There already is a tag called \"{other.Name}\".");

        var description = dto.Description?.Trim();
        if (description?.Length > TagRules.MaxDescriptionLength)
            throw new InvalidDataException($"A tag's description can be at most {TagRules.MaxDescriptionLength} characters long.");

        return (name, string.IsNullOrEmpty(description) ? null : description);
    }

    /// <summary>Throws unless the tree, with <paramref name="id"/>'s inclusions replaced by the requested ones, is still valid.</summary>
    private static void CheckTree(TagSnapshot snapshot, int id, string name, TagEditDto dto)
    {
        foreach (var other in dto.IncludesIds.Concat(dto.IncludedInIds))
        {
            if (!snapshot.ById.ContainsKey(other))
                throw new InvalidDataException($"There is no tag with id {other} (any more). Reload the page.");
        }

        var changed = snapshot.Graph.WithInclusions(id, dto.IncludesIds.Distinct(), dto.IncludedInIds.Distinct());
        if (changed.Problem(t => t == id ? name : snapshot.NameOf(t)) is { } problem)
            throw new InvalidDataException(problem);
    }

    private void AddInclusions(int id, TagEditDto dto)
    {
        db.TagInclusions.AddRange(dto.IncludesIds.Distinct().Select(c => new TagInclusion { ParentId = id, ChildId = c }));
        db.TagInclusions.AddRange(dto.IncludedInIds.Distinct().Select(p => new TagInclusion { ParentId = p, ChildId = id }));
    }

    // ── For the mod pages ───────────────────────────────────────────────────

    /// <summary>
    /// What a filter by these tag names finds: per name, the tag and every tag it includes. A mod must have a tag from
    /// each set. Null when a name is no tag (any more), so nothing can match.
    /// </summary>
    public async Task<List<HashSet<int>>?> FilterSetsAsync(IEnumerable<string> names, CancellationToken ct = default)
    {
        var snapshot = await index.GetAsync(db, ct);
        var sets = new List<HashSet<int>>();
        foreach (var name in names.Select(TagRules.Clean).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (snapshot.Find(name) is not { } tag)
                return null;
            sets.Add(snapshot.Graph.SelfAndDescendants(tag.Id));
        }
        return sets;
    }

    /// <summary>The tags whose name contains <paramref name="text"/>, with every tag they include: what a text search finds through tags.</summary>
    public async Task<HashSet<int>> MatchingTagsAsync(string text, CancellationToken ct = default)
    {
        var snapshot = await index.GetAsync(db, ct);
        return [.. snapshot.Tags
            .Where(t => t.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .SelectMany(t => snapshot.Graph.SelfAndDescendants(t.Id))];
    }

    /// <summary>Fills <see cref="Mod.TagIds"/> of each mod with its group's tags.</summary>
    public async Task FillTagIdsAsync(IReadOnlyCollection<Mod> mods, CancellationToken ct = default)
    {
        var groupIds = mods.Select(m => m.ModGroupId).Distinct().ToList();
        if (groupIds.Count == 0)
            return;

        var byGroup = (await db.ModGroupTags.AsNoTracking()
                .Where(t => groupIds.Contains(t.ModGroupId))
                .Select(t => new { t.ModGroupId, t.TagId })
                .ToListAsync(ct))
            .GroupBy(t => t.ModGroupId)
            .ToDictionary(g => g.Key, g => g.Select(t => t.TagId).Order().ToList());

        foreach (var mod in mods)
            mod.TagIds = byGroup.TryGetValue(mod.ModGroupId, out var ids) ? [.. ids] : [];
    }

    /// <summary>Why a mod can't have these tags (unknown, or too many); null when it can.</summary>
    public async Task<string?> TagIdsProblemAsync(IEnumerable<int> tagIds, CancellationToken ct = default)
    {
        var ids = tagIds.Distinct().ToList();
        if (ids.Count > TagRules.MaxTagsPerMod)
            return $"A mod can have at most {TagRules.MaxTagsPerMod} tags.";

        var snapshot = await index.GetAsync(db, ct);
        return ids.FirstOrDefault(id => !snapshot.ById.ContainsKey(id)) is var unknown and not 0
            ? $"There is no tag with id {unknown} (any more). Reload the page."
            : null;
    }

    /// <summary>
    /// Replaces the tags of a mod group (not saved yet). Throws <see cref="InvalidDataException"/> for an unknown tag
    /// or too many.
    /// </summary>
    public async Task SetGroupTagsAsync(int groupId, IEnumerable<int> tagIds, CancellationToken ct = default)
    {
        var ids = tagIds.Distinct().ToList();
        if (await TagIdsProblemAsync(ids, ct) is { } problem)
            throw new InvalidDataException(problem);

        var current = await db.ModGroupTags.Where(t => t.ModGroupId == groupId).ToListAsync(ct);
        db.ModGroupTags.RemoveRange(current.Where(t => !ids.Contains(t.TagId)));
        db.ModGroupTags.AddRange(ids.Where(id => current.All(t => t.TagId != id)).Select(id => new ModGroupTag { ModGroupId = groupId, TagId = id }));
    }
}
