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
public sealed class TagService(AppDbContext db, TagIndex index, CatalogService catalog, SiteSettings settings, ILogger<TagService> logger) : ITagsApi
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
                TypeId = t.TypeId,
                IsProposed = t.IsProposed,
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

    public Task<TagDto> Create(TagEditDto dto, CancellationToken ct = default) => Create(dto, null, ct);

    /// <summary>Creates a tag; with <paramref name="proposedById"/> a proposed one that account created.</summary>
    public async Task<TagDto> Create(TagEditDto dto, int? proposedById, CancellationToken ct = default)
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
            var tag = new Tag
            {
                Name = name,
                NormalizedName = TagRules.Normalize(name),
                Description = description,
                IsProposed = proposedById is not null,
                ProposedById = proposedById
            };
            db.Tags.Add(tag);
            await db.SaveChangesAsync(ct);
            AddInclusions(tag.Id, dto);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            id = tag.Id;
            index.Invalidate();
            if (proposedById is null)
                logger.LogInformation("Tag {TagId} ({Name}) was created.", id, name);
            else
                logger.LogInformation("Tag {TagId} ({Name}) was proposed by user {UserId}.", id, name, proposedById);
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
            if (tag.TypeId is not null)
                throw new InvalidDataException($"{tag.Name} is the tag of a mod type, so it can't be deleted.");
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

            // A type tag on a mod means its files are of that type, so no other tag can turn into one or take its place.
            if ((source.TypeId ?? target.TypeId) is not null)
            {
                throw new InvalidDataException(
                    $"{(source.TypeId is not null ? source.Name : target.Name)} is the tag of a mod type, so it can't be merged. Mods get it from their files' types.");
            }

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

            // Suggestions: to the target, unless the mod has it or a suggestion of it already (those go with the source).
            var suggestions = await db.TagSuggestions.Where(s => s.TagId == id).ToListAsync(ct);
            var suggestedGroups = suggestions.Select(s => s.ModGroupId).ToList();
            var targetTaken = (await db.TagSuggestions.Where(s => s.TagId == targetId && suggestedGroups.Contains(s.ModGroupId))
                    .Select(s => s.ModGroupId).ToListAsync(ct))
                .Concat(groups).Concat(haveTarget).ToHashSet();
            foreach (var suggestion in suggestions.Where(s => !targetTaken.Contains(s.ModGroupId)))
                suggestion.TagId = targetId;

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

        // A type tag including another tag would make the mods tagged with that one count as of the type, whatever their
        // files are. Type tags can be included by ordinary tags ("Missions" including both mission types).
        if (dto.IncludesIds.Count > 0 && snapshot.ById.GetValueOrDefault(id)?.TypeId is not null)
            throw new InvalidDataException($"{name} is the tag of a mod type, so it can't include other tags.");
        if (dto.IncludedInIds.Select(p => snapshot.ById[p]).FirstOrDefault(p => p.TypeId is not null) is { } typeParent)
            throw new InvalidDataException($"{typeParent.Name} is the tag of a mod type, so it can't include other tags.");

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

        var threshold = await settings.TagSuggestionThresholdAsync(ct);
        var communityByGroup = (await CountingSuggestions(threshold)
                .Where(s => groupIds.Contains(s.ModGroupId))
                .Select(s => new { s.ModGroupId, TagId = s.TagId!.Value })
                .ToListAsync(ct))
            .GroupBy(s => s.ModGroupId)
            .ToDictionary(g => g.Key, g => g.Select(s => s.TagId).Order().ToList());

        foreach (var mod in mods)
        {
            mod.TagIds = byGroup.TryGetValue(mod.ModGroupId, out var ids) ? [.. ids] : [];
            mod.CommunityTagIds = communityByGroup.TryGetValue(mod.ModGroupId, out var community) ? [.. community] : [];
        }
    }

    /// <summary>
    /// The open suggestions of a tag whose score reached <paramref name="threshold"/>: they count in filters and searches
    /// like the mods' own tags until the owner or an admin decides.
    /// </summary>
    public IQueryable<TagSuggestion> CountingSuggestions(int threshold) =>
        db.TagSuggestions.Where(s => s.State == TagSuggestionState.Open && s.TagId != null && s.Score >= threshold);

    /// <summary>
    /// Why a mod can't have these tags (unknown, or too many); null when it can. Type tags among them don't count:
    /// <see cref="SetGroupTagsAsync"/> ignores them.
    /// </summary>
    public async Task<string?> TagIdsProblemAsync(IEnumerable<int> tagIds, CancellationToken ct = default)
    {
        var snapshot = await index.GetAsync(db, ct);
        var ids = tagIds.Distinct().ToList();
        if (ids.FirstOrDefault(id => !snapshot.ById.ContainsKey(id)) is var unknown and not 0)
            return $"There is no tag with id {unknown} (any more). Reload the page.";

        return ids.Count(id => snapshot.ById[id].TypeId is null) > TagRules.MaxTagsPerMod
            ? $"A mod can have at most {TagRules.MaxTagsPerMod} tags."
            : null;
    }

    /// <summary>
    /// Replaces the ordinary tags of a mod group (not saved yet). Type tags are left as they are, whether asked for or
    /// not: they follow the files (<see cref="SyncTypeTagsAsync"/>). Throws <see cref="InvalidDataException"/> for an
    /// unknown tag or too many.
    /// </summary>
    public async Task SetGroupTagsAsync(int groupId, IEnumerable<int> tagIds, int? decidedById = null, CancellationToken ct = default)
    {
        var requested = tagIds.ToList();
        if (await TagIdsProblemAsync(requested, ct) is { } problem)
            throw new InvalidDataException(problem);

        var snapshot = await index.GetAsync(db, ct);
        bool Ordinary(int id) => snapshot.ById.TryGetValue(id, out var tag) && tag.TypeId is null;
        var ids = requested.Where(Ordinary).Distinct().ToList();

        var current = (await db.ModGroupTags.Where(t => t.ModGroupId == groupId).ToListAsync(ct)).Where(t => Ordinary(t.TagId)).ToList();
        db.ModGroupTags.RemoveRange(current.Where(t => !ids.Contains(t.TagId)));
        var added = ids.Where(id => current.All(t => t.TagId != id)).ToList();
        db.ModGroupTags.AddRange(added.Select(id => new ModGroupTag { ModGroupId = groupId, TagId = id }));

        // Users suggested a tag the owner now added themselves: that suggestion is approved.
        var settled = await db.TagSuggestions
            .Where(s => s.ModGroupId == groupId && s.State == TagSuggestionState.Open && s.TagId != null && added.Contains(s.TagId.Value))
            .ToListAsync(ct);
        foreach (var suggestion in settled)
        {
            suggestion.State = TagSuggestionState.Approved;
            suggestion.DecidedById = decidedById;
            suggestion.DecidedAt = DateTime.UtcNow;
        }
    }

    // ── Type tags ───────────────────────────────────────────────────────────

    /// <summary>
    /// Gives the mod group the type tags of its latest version (<see cref="ModTemplates.TypeIdsOf"/>) and removes the
    /// others, and saves. Call after anything that changes the latest version or its files.
    /// </summary>
    public async Task SyncTypeTagsAsync(int groupId, CancellationToken ct = default)
    {
        var latest = await db.Mods.AsNoTracking().Include(m => m.Files)
            .FirstOrDefaultAsync(m => m.ModGroupId == groupId && m.IsLatestVersion, ct);
        if (latest is null)
            return;

        var snapshot = await index.GetAsync(db, ct);
        var typeIds = ModTemplates.TypeIdsOf(latest.Files.Select(f => f.Template), latest.Category);
        var typeTags = snapshot.Tags.Where(t => t.TypeId is not null).ToList();
        var wanted = typeTags.Where(t => typeIds.Contains(t.TypeId!)).Select(t => t.Id).ToList();
        var typeTagIds = typeTags.Select(t => t.Id).ToList();

        var current = await db.ModGroupTags.Where(t => t.ModGroupId == groupId && typeTagIds.Contains(t.TagId)).ToListAsync(ct);
        var removed = current.Where(t => !wanted.Contains(t.TagId)).ToList();
        var added = wanted.Where(id => current.All(t => t.TagId != id)).Select(id => new ModGroupTag { ModGroupId = groupId, TagId = id }).ToList();
        if (removed.Count == 0 && added.Count == 0)
            return;

        db.ModGroupTags.RemoveRange(removed);
        db.ModGroupTags.AddRange(added);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Creates the tag of every mod type that has none yet (the AddTags migration made the first ones; this covers types
    /// added later). A name an ordinary tag already has gets " (type)" added. Run at startup.
    /// </summary>
    public async Task EnsureTypeTagsAsync(CancellationToken ct = default)
    {
        await index.WriteLock.WaitAsync(ct);
        try
        {
            var snapshot = await TagIndex.LoadAsync(db, ct);
            var missing = ModTemplates.All
                .Where(t => t.Id != ModTemplates.Custom && snapshot.Tags.All(tag => tag.TypeId != t.Id))
                .ToList();
            if (missing.Count == 0)
                return;

            foreach (var template in missing)
            {
                var name = snapshot.Find(template.Name) is null ? template.Name : $"{template.Name} (type)";
                db.Tags.Add(new Tag { Name = name, NormalizedName = TagRules.Normalize(name), TypeId = template.Id });
                logger.LogInformation("Created the tag {Name} for the mod type {TypeId}.", name, template.Id);
            }
            await db.SaveChangesAsync(ct);
            index.Invalidate();
        }
        finally
        {
            index.WriteLock.Release();
        }
    }
}
