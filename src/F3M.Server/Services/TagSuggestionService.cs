using F3M.Server.Data;
using F3M.Server.Helpers;
using F3M.Server.Models;
using F3M.Server.Statistics;
using F3M.Shared;
using F3M.Shared.Api;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace F3M.Server.Services;

/// <summary>
/// Server side of <see cref="ITagSuggestionsApi"/>: users suggest tags for mods, create new tags, and vote on both.
/// An open suggestion whose score reached the limit counts in filters and searches (see ModsService.GetMods); the
/// mod's owner or an admin approves it onto the mod or rejects it. Owners don't vote on their own mods: they decide.
/// </summary>
public sealed class TagSuggestionService(
    AppDbContext db, TagService tags, TagIndex index, SiteSettings settings, CatalogService catalog, StatsRecorder stats,
    IHttpContextAccessor httpContextAccessor, ILogger<TagSuggestionService> logger) : ITagSuggestionsApi
{
    private const int MaxQueueSize = 500;

    private ClaimsPrincipal Principal =>
        httpContextAccessor.HttpContext?.User
        ?? throw new InvalidOperationException("TagSuggestionService was called outside of an HTTP request.");

    private int? UserId => Helper.GetUserId(Principal);
    private bool IsAdmin => Principal.IsInRole(AppRoles.Admin);

    private int RequireUser() => UserId ?? throw new UnauthorizedAccessException("No signed-in user.");

    // ── The mod page ────────────────────────────────────────────────────────

    public async Task<ModTagSuggestions> GetForMod(int groupId, CancellationToken ct = default)
    {
        var group = await db.ModGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == groupId, ct)
                    ?? throw new KeyNotFoundException($"No mod group with id {groupId} was found.");
        var limits = await settings.TagSuggestionLimitsAsync(ct);
        var suggestions = await db.TagSuggestions.AsNoTracking().Include(s => s.Votes)
            .Where(s => s.ModGroupId == groupId && s.State == TagSuggestionState.Open)
            .ToListAsync(ct);

        var userId = UserId;
        var names = await UserNamesAsync(suggestions.Select(s => s.SuggestedById), ct);
        var rejected = await db.TagSuggestions.AsNoTracking()
            .Where(s => s.ModGroupId == groupId && s.State == TagSuggestionState.Rejected && s.TagId != null)
            .Select(s => s.TagId!.Value)
            .ToListAsync(ct);
        return new ModTagSuggestions
        {
            Items = [.. suggestions
                .OrderByDescending(s => s.Score).ThenBy(s => s.CreatedAt)
                .Select(s => ToDto(s, limits.Threshold, userId, names))],
            Threshold = limits.Threshold,
            TagThreshold = limits.TagThreshold,
            CanVote = userId is { } voter && voter != group.OwnerId,
            CanDecide = userId is { } decider && (IsAdmin || decider == group.OwnerId),
            IsAdmin = userId is not null && IsAdmin,
            RejectedTagIds = rejected,
            RemainingToday = userId is null ? 0 : IsAdmin ? null : Math.Max(0, limits.DailyLimit - await StartedTodayAsync(userId.Value, ct))
        };
    }

    public async Task<TagSuggestionDto> Suggest(int groupId, TagSuggestRequest request, CancellationToken ct = default)
    {
        var userId = RequireUser();
        var group = await db.ModGroups.FindAsync([groupId], ct) ?? throw new KeyNotFoundException($"No mod group with id {groupId} was found.");
        if (group.OwnerId == userId)
            throw new InvalidDataException("It's your mod: add the tag on its edit page instead.");

        var snapshot = await index.GetAsync(db, ct);
        var reason = request.Reason?.Trim() is { Length: > 0 } r ? r : null;
        if (reason?.Length > TagSuggestionLimits.MaxReasonLength)
            throw new InvalidDataException($"The reason can be at most {TagSuggestionLimits.MaxReasonLength} characters long.");

        // A new name that is a tag already is a suggestion of that tag; otherwise the tag is created, as a proposed one.
        var tagId = request.TagId;
        string? createdName = null;
        if (tagId is null)
        {
            var name = TagRules.Clean(request.NewTagName);
            if (TagRules.NameProblem(name) is { } problem)
                throw new InvalidDataException(problem);
            if (snapshot.Find(name) is { } same)
            {
                tagId = same.Id;
            }
            else
            {
                tagId = await ProposeTagAsync(name, request.ParentTagId, request.NewTagDescription, userId, snapshot, ct);
                createdName = name;
            }
        }

        TagSuggestion? existing = null;
        if (createdName is null)
        {
            var id = tagId.Value;
            var tag = snapshot.ById.GetValueOrDefault(id) ?? throw new InvalidDataException($"There is no tag with id {id} (any more). Reload the page.");
            if (tag.TypeId is not null)
                throw new InvalidDataException($"{tag.Name} is the tag of a mod type: mods get it from their files' types.");
            if (await ImpliedTagAsync(groupId, id, snapshot, ct) is { } because)
                throw new InvalidDataException(because);

            existing = await db.TagSuggestions.Include(s => s.Votes).FirstOrDefaultAsync(s => s.ModGroupId == groupId && s.TagId == id, ct);
        }

        if (existing is not null)
        {
            if (existing.State != TagSuggestionState.Open)
                throw new InvalidDataException(existing.State == TagSuggestionState.Rejected
                    ? "That tag was rejected for this mod, so it can't be suggested again."
                    : "That suggestion was already decided.");

            // Suggested already: the suggestion gets this account's vote.
            SetVote(existing, userId, 1);
            await db.SaveChangesAsync(ct);
            return await DtoAsync(existing, ct);
        }

        if (createdName is null)
            await CheckDailyLimitAsync(userId, ct);

        // A suggestion that created its tag keeps the name, for the statistics.
        var suggestion = new TagSuggestion
        {
            ModGroupId = groupId,
            TagId = tagId,
            RequestedName = createdName,
            Reason = reason,
            SuggestedById = userId,
            State = TagSuggestionState.Open
        };
        SetVote(suggestion, userId, 1);
        db.TagSuggestions.Add(suggestion);
        await db.SaveChangesAsync(ct);

        var label = createdName ?? snapshot.NameOf(tagId!.Value);
        stats.Count(createdName is null ? StatsRecorder.Metrics.TagSuggested : StatsRecorder.Metrics.TagRequested, label);
        logger.LogInformation("The tag {Tag} was suggested for mod group {GroupId}.", label, groupId);
        return await DtoAsync(suggestion, ct);
    }

    /// <summary>Creates the tag a user named, as a proposed tag with their vote; returns its id.</summary>
    private async Task<int> ProposeTagAsync(string name, int? parentTagId, string? description, int userId, TagSnapshot snapshot, CancellationToken ct)
    {
        await CheckDailyLimitAsync(userId, ct);

        int? parentId = null;
        if (parentTagId is { } wanted)
        {
            var parent = snapshot.ById.GetValueOrDefault(wanted)
                         ?? throw new InvalidDataException($"There is no tag with id {wanted} (any more). Reload the page.");
            if (parent.TypeId is not null)
                throw new InvalidDataException($"{parent.Name} is the tag of a mod type: it can't have tags under it.");
            parentId = parent.Id;
        }

        // TagService checks the name, the description and the tree like on the tags page.
        var created = await tags.Create(new TagEditDto
        {
            Name = name,
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            IncludedInIds = parentId is { } p ? [p] : []
        }, userId, ct);

        var tag = await db.Tags.FindAsync([created.Id], ct) ?? throw new KeyNotFoundException($"No tag with id {created.Id} was found.");
        await SetProposalVoteAsync(tag, userId, 1, ct);
        await db.SaveChangesAsync(ct);
        index.Invalidate();
        return tag.Id;
    }

    private async Task CheckDailyLimitAsync(int userId, CancellationToken ct)
    {
        if (IsAdmin)
            return;
        var limit = (await settings.TagSuggestionLimitsAsync(ct)).DailyLimit;
        if (await StartedTodayAsync(userId, ct) >= limit)
            throw new InvalidDataException($"You can suggest or create at most {limit} tags a day. Vote on the open ones instead, or try again tomorrow.");
    }

    // ── Tags users created ──────────────────────────────────────────────────

    public async Task<TagProposalInfo> GetProposalInfo(CancellationToken ct = default)
    {
        var limits = await settings.TagSuggestionLimitsAsync(ct);
        var userId = UserId;
        return new TagProposalInfo
        {
            TagThreshold = limits.TagThreshold,
            CanCreate = userId is not null,
            RemainingToday = userId is null ? 0 : IsAdmin ? null : Math.Max(0, limits.DailyLimit - await StartedTodayAsync(userId.Value, ct))
        };
    }

    public async Task<TagProposalDto> Propose(TagProposeRequest request, CancellationToken ct = default)
    {
        var userId = RequireUser();
        var name = TagRules.Clean(request.Name);
        if (TagRules.NameProblem(name) is { } problem)
            throw new InvalidDataException(problem);

        var snapshot = await index.GetAsync(db, ct);
        if (snapshot.Find(name) is { } same)
            throw new InvalidDataException($"There's a tag {same.Name} already.");

        var tagId = await ProposeTagAsync(name, request.ParentTagId, request.Description, userId, snapshot, ct);
        stats.Count(StatsRecorder.Metrics.TagRequested, name);
        logger.LogInformation("The tag {Tag} was created on the tags page, to be voted on.", name);
        return await ProposalDtoAsync(tagId, ct);
    }

    public async Task<List<TagProposalDto>> GetProposals(CancellationToken ct = default)
    {
        var proposed = await db.Tags.AsNoTracking().Where(t => t.IsProposed).OrderByDescending(t => t.CreatedAt).ToListAsync(ct);
        if (proposed.Count == 0)
            return [];

        var ids = proposed.Select(t => t.Id).ToList();
        var votes = await db.TagProposalVotes.AsNoTracking().Where(v => ids.Contains(v.TagId)).ToListAsync(ct);
        var onMods = await db.ModGroupTags.AsNoTracking().Where(t => ids.Contains(t.TagId))
            .Select(t => new { t.TagId, t.ModGroupId }).ToListAsync(ct);
        var suggested = await db.TagSuggestions.AsNoTracking()
            .Where(s => s.TagId != null && ids.Contains(s.TagId.Value) && s.State == TagSuggestionState.Open)
            .Select(s => new { TagId = s.TagId!.Value, s.ModGroupId }).ToListAsync(ct);
        var names = await UserNamesAsync(proposed.Select(t => t.ProposedById).Concat(IsAdmin ? votes.Select(v => (int?)v.UserId) : []), ct);
        var snapshot = await index.GetAsync(db, ct);
        var userId = UserId;

        return [.. proposed.Select(t =>
        {
            var mine = votes.Where(v => v.TagId == t.Id).ToList();
            return new TagProposalDto
            {
                TagId = t.Id,
                Name = t.Name,
                Description = t.Description,
                ParentIds = [.. snapshot.Graph.ParentsOf(t.Id).Order()],
                Score = t.ProposalScore,
                Up = mine.Count(v => v.Value > 0),
                Down = mine.Count(v => v.Value < 0),
                MyVote = userId is { } u ? mine.FirstOrDefault(v => v.UserId == u)?.Value ?? 0 : 0,
                IsProposed = true,
                ProposedBy = t.ProposedById is { } by ? names.GetValueOrDefault(by) : null,
                CreatedAt = t.CreatedAt,
                ModCount = onMods.Where(m => m.TagId == t.Id).Select(m => m.ModGroupId)
                    .Concat(suggested.Where(s => s.TagId == t.Id).Select(s => s.ModGroupId)).Distinct().Count(),
                UpVoters = IsAdmin ? [.. mine.Where(v => v.Value > 0).Select(v => names.GetValueOrDefault(v.UserId, "?")).Order()] : [],
                DownVoters = IsAdmin ? [.. mine.Where(v => v.Value < 0).Select(v => names.GetValueOrDefault(v.UserId, "?")).Order()] : []
            };
        })];
    }

    public async Task<TagProposalDto> VoteProposal(int tagId, TagVoteRequest vote, CancellationToken ct = default)
    {
        var userId = RequireUser();
        if (vote.Value is < -1 or > 1)
            throw new InvalidDataException("A vote is 1, -1 or 0.");

        var tag = await FindProposedAsync(tagId, ct);
        await SetProposalVoteAsync(tag, userId, vote.Value, ct);
        await db.SaveChangesAsync(ct);
        index.Invalidate();
        return await ProposalDtoAsync(tagId, ct);
    }

    public async Task<TagProposalDto> AcceptProposal(int tagId, CancellationToken ct = default)
    {
        var tag = await FindProposedAsync(tagId, ct);
        Accept(tag, "by an admin");
        await db.SaveChangesAsync(ct);
        index.Invalidate();
        return await ProposalDtoAsync(tagId, ct);
    }

    public async Task RejectProposal(int tagId, CancellationToken ct = default)
    {
        var tag = await FindProposedAsync(tagId, ct);
        logger.LogInformation("The proposed tag {Tag} was rejected.", tag.Name);
        await tags.Delete(tag.Id, ct);
    }

    private async Task<Tag> FindProposedAsync(int tagId, CancellationToken ct)
    {
        var tag = await db.Tags.FindAsync([tagId], ct) ?? throw new KeyNotFoundException($"No tag with id {tagId} was found.");
        if (!tag.IsProposed)
            throw new InvalidDataException($"{tag.Name} is a regular tag already.");
        return tag;
    }

    /// <summary>Sets, changes or (with 0) takes back the account's vote on a proposed tag; enough points accept it.</summary>
    private async Task SetProposalVoteAsync(Tag tag, int userId, int value, CancellationToken ct)
    {
        var votes = await db.TagProposalVotes.Where(v => v.TagId == tag.Id).ToListAsync(ct);
        var vote = votes.FirstOrDefault(v => v.UserId == userId);
        if (value == 0)
        {
            if (vote is not null)
            {
                db.TagProposalVotes.Remove(vote);
                votes.Remove(vote);
            }
        }
        else if (vote is null)
        {
            vote = new TagProposalVote { TagId = tag.Id, UserId = userId, Value = value };
            db.TagProposalVotes.Add(vote);
            votes.Add(vote);
        }
        else
        {
            vote.Value = value;
        }

        tag.ProposalScore = votes.Sum(v => v.Value);
        if (tag.ProposalScore >= (await settings.TagSuggestionLimitsAsync(ct)).TagThreshold)
            Accept(tag, $"with {tag.ProposalScore} points");
    }

    /// <summary>Makes the tag a regular one (not saved yet; invalidate the tag index after saving).</summary>
    private void Accept(Tag tag, string how)
    {
        tag.IsProposed = false;
        logger.LogInformation("The proposed tag {Tag} was accepted {How}.", tag.Name, how);
    }

    private async Task<TagProposalDto> ProposalDtoAsync(int tagId, CancellationToken ct)
    {
        var tag = await db.Tags.AsNoTracking().FirstAsync(t => t.Id == tagId, ct);
        if (tag.IsProposed)
            return (await GetProposals(ct)).First(p => p.TagId == tagId);

        // Accepted: no longer in the list of proposals.
        return new TagProposalDto { TagId = tag.Id, Name = tag.Name, Description = tag.Description, Score = tag.ProposalScore, IsProposed = false };
    }

    public async Task<TagSuggestionDto> Vote(int id, TagVoteRequest vote, CancellationToken ct = default)
    {
        var userId = RequireUser();
        if (vote.Value is < -1 or > 1)
            throw new InvalidDataException("A vote is 1, -1 or 0.");

        var suggestion = await FindAsync(id, ct);
        if (suggestion.State != TagSuggestionState.Open)
            throw new InvalidDataException("That suggestion was already decided.");
        var ownerId = await db.ModGroups.Where(g => g.Id == suggestion.ModGroupId).Select(g => g.OwnerId).FirstAsync(ct);
        if (ownerId == userId)
            throw new InvalidDataException("It's your mod: approve or reject the suggestion instead of voting.");

        SetVote(suggestion, userId, vote.Value);
        await db.SaveChangesAsync(ct);
        return await DtoAsync(suggestion, ct);
    }

    public async Task<TagSuggestionDto> Approve(int id, CancellationToken ct = default)
    {
        var suggestion = await FindAsync(id, ct);
        await EnsureCanDecideAsync(suggestion, ct);
        if (suggestion.TagId is null)
            throw new InvalidDataException("That tag doesn't exist yet. An admin creates it on the tags page first.");
        if (suggestion.State != TagSuggestionState.Open)
            throw new InvalidDataException("That suggestion was already decided.");

        await ApproveAsync(suggestion, ct);
        await db.SaveChangesAsync(ct);
        await catalog.TryRefreshIndexAsync(ct);
        return await DtoAsync(suggestion, ct);
    }

    public async Task<TagSuggestionDto> Reject(int id, CancellationToken ct = default)
    {
        var suggestion = await FindAsync(id, ct);
        await EnsureCanDecideAsync(suggestion, ct);
        if (suggestion.State != TagSuggestionState.Open)
            throw new InvalidDataException("That suggestion was already decided.");

        Decide(suggestion, TagSuggestionState.Rejected);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("The suggestion {SuggestionId} ({Tag}) for mod group {GroupId} was rejected.",
            suggestion.Id, await LabelAsync(suggestion, ct), suggestion.ModGroupId);
        return await DtoAsync(suggestion, ct);
    }

    // ── Admins ──────────────────────────────────────────────────────────────

    public async Task<List<TagSuggestionAdminDto>> GetQueue(TagSuggestionState? state = TagSuggestionState.Open, CancellationToken ct = default)
    {
        var threshold = await settings.TagSuggestionThresholdAsync(ct);
        var query = db.TagSuggestions.AsNoTracking().Include(s => s.Votes).AsQueryable();
        if (state is { } only)
            query = query.Where(s => s.State == only);

        var suggestions = await query
            .OrderByDescending(s => s.CreatedAt)
            .Take(MaxQueueSize)
            .ToListAsync(ct);

        var groupIds = suggestions.Select(s => s.ModGroupId).Distinct().ToList();
        var mods = await db.Mods.AsNoTracking()
            .Where(m => m.IsLatestVersion && groupIds.Contains(m.ModGroupId))
            .Select(m => new { m.ModGroupId, m.Id, m.Name })
            .ToDictionaryAsync(m => m.ModGroupId, ct);
        var names = await UserNamesAsync(suggestions
            .SelectMany(s => s.Votes.Select(v => (int?)v.UserId))
            .Concat(suggestions.Select(s => s.SuggestedById))
            .Concat(suggestions.Select(s => s.DecidedById)), ct);

        return [.. suggestions
            .Select(s =>
            {
                var dto = Fill(new TagSuggestionAdminDto(), s, threshold, null, names);
                dto.ModName = mods.TryGetValue(s.ModGroupId, out var mod) ? mod.Name : $"Mod group {s.ModGroupId}";
                dto.ModId = mod?.Id ?? 0;
                dto.UpVoters = [.. s.Votes.Where(v => v.Value > 0).Select(v => names.GetValueOrDefault(v.UserId, "?")).Order()];
                dto.DownVoters = [.. s.Votes.Where(v => v.Value < 0).Select(v => names.GetValueOrDefault(v.UserId, "?")).Order()];
                dto.DecidedBy = s.DecidedById is { } d ? names.GetValueOrDefault(d) : null;
                dto.DecidedAt = s.DecidedAt;
                return dto;
            })
            // Open ones that count first: they already change what visitors find.
            .OrderByDescending(s => s.Counts)
            .ThenByDescending(s => s.State == TagSuggestionState.Open ? s.Score : int.MinValue)
            .ThenByDescending(s => s.CreatedAt)];
    }

    /// <summary>Open suggestions and proposed tags.</summary>
    public async Task<int> GetOpenCount(CancellationToken ct = default) =>
        await db.TagSuggestions.CountAsync(s => s.State == TagSuggestionState.Open, ct)
        + await db.Tags.CountAsync(t => t.IsProposed, ct);

    public async Task<TagSuggestionDto> CreateTag(int id, TagEditDto tag, CancellationToken ct = default)
    {
        var request = await FindAsync(id, ct);
        if (request.TagId is not null || request.State != TagSuggestionState.Open)
            throw new InvalidDataException("That isn't an open request for a new tag.");

        // TagService checks the name and the tree like on the tags page.
        var created = await tags.Create(tag, ct);

        // This request and the other open ones for the same name become suggestions of the new tag, votes and all.
        var requests = await db.TagSuggestions
            .Where(s => s.TagId == null && s.State == TagSuggestionState.Open && s.RequestedNormalizedName == request.RequestedNormalizedName)
            .ToListAsync(ct);
        foreach (var r in requests)
            r.TagId = created.Id;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("The tag {Tag} was created from the request {SuggestionId} ({Requested}); {Count} requests now suggest it.",
            created.Name, request.Id, request.RequestedName, requests.Count);
        return await DtoAsync(request, ct);
    }

    public async Task<int> ApproveCounting(CancellationToken ct = default)
    {
        var threshold = await settings.TagSuggestionThresholdAsync(ct);
        var counting = await db.TagSuggestions
            .Where(s => s.State == TagSuggestionState.Open && s.TagId != null && s.Score >= threshold)
            .OrderByDescending(s => s.Score)
            .ToListAsync(ct);

        var approved = 0;
        foreach (var suggestion in counting)
        {
            // Settled along with a more specific one approved before it.
            if (suggestion.State != TagSuggestionState.Open)
                continue;
            try
            {
                await ApproveAsync(suggestion, ct);
                await db.SaveChangesAsync(ct);
                approved++;
            }
            catch (InvalidDataException)
            {
                // The mod has the most tags it can have; that one stays open for a look by hand.
            }
        }

        if (approved > 0)
            await catalog.TryRefreshIndexAsync(ct);
        return approved;
    }

    public async Task Delete(int id, CancellationToken ct = default)
    {
        var suggestion = await FindAsync(id, ct);
        if (suggestion.State != TagSuggestionState.Rejected)
            throw new InvalidDataException("Only rejected suggestions can be deleted.");

        db.TagSuggestions.Remove(suggestion);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Deleted the rejected suggestion {SuggestionId} ({Tag}) for mod group {GroupId}.",
            suggestion.Id, await LabelAsync(suggestion, ct), suggestion.ModGroupId);
    }

    public async Task<int> DeleteRejected(CancellationToken ct = default)
    {
        var count = await db.TagSuggestions.Where(s => s.State == TagSuggestionState.Rejected).ExecuteDeleteAsync(ct);
        if (count > 0)
            logger.LogInformation("Deleted {Count} rejected tag suggestions.", count);
        return count;
    }

    public Task<TagSuggestionLimits> GetLimits(CancellationToken ct = default) => settings.TagSuggestionLimitsAsync(ct);

    public async Task<TagSuggestionLimits> SetLimits(TagSuggestionLimits limits, CancellationToken ct = default)
    {
        if (limits.Threshold is < 1 or > 1000 || limits.DailyLimit is < 1 or > 1000 || limits.TagThreshold is < 1 or > 1000)
            throw new InvalidDataException("Every number must be between 1 and 1000.");

        await settings.SetTagSuggestionLimitsAsync(limits, ct);
        logger.LogInformation("Tag suggestions now count from a score of {Threshold}; accounts may start {DailyLimit} a day; proposed tags are accepted from {TagThreshold}.",
            limits.Threshold, limits.DailyLimit, limits.TagThreshold);

        // A lower limit accepts the proposed tags that have enough points now.
        foreach (var tag in await db.Tags.Where(t => t.IsProposed && t.ProposalScore >= limits.TagThreshold).ToListAsync(ct))
            Accept(tag, $"with {tag.ProposalScore} points, after the limit changed");
        await db.SaveChangesAsync(ct);
        index.Invalidate();
        return await settings.TagSuggestionLimitsAsync(ct);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<TagSuggestion> FindAsync(int id, CancellationToken ct) =>
        await db.TagSuggestions.Include(s => s.Votes).FirstOrDefaultAsync(s => s.Id == id, ct)
        ?? throw new KeyNotFoundException($"No tag suggestion with id {id} was found.");

    /// <summary>The mod's owner or an admin; otherwise <see cref="UnauthorizedAccessException"/> (403).</summary>
    private async Task EnsureCanDecideAsync(TagSuggestion suggestion, CancellationToken ct)
    {
        var userId = RequireUser();
        if (IsAdmin)
            return;
        var ownerId = await db.ModGroups.Where(g => g.Id == suggestion.ModGroupId).Select(g => g.OwnerId).FirstOrDefaultAsync(ct);
        if (ownerId != userId)
            throw new UnauthorizedAccessException("Only the mod's owner or an admin decides on its tag suggestions.");
    }

    /// <summary>
    /// Puts the tag on the mod and marks the suggestion approved (not saved yet). A more specific tag replaces the mod's
    /// tags it's a kind of: the mod still counts as those, and it frees up room.
    /// </summary>
    private async Task ApproveAsync(TagSuggestion suggestion, CancellationToken ct)
    {
        var tagId = suggestion.TagId!.Value;
        var snapshot = await index.GetAsync(db, ct);
        var current = await db.ModGroupTags.Where(t => t.ModGroupId == suggestion.ModGroupId).ToListAsync(ct);
        if (current.All(t => t.TagId != tagId))
        {
            var ancestors = snapshot.Graph.SelfAndAncestors(tagId);
            var replaced = current.Where(t => ancestors.Contains(t.TagId) && snapshot.ById.GetValueOrDefault(t.TagId)?.TypeId is null).ToList();
            var ordinary = current.Count(t => snapshot.ById.GetValueOrDefault(t.TagId)?.TypeId is null) - replaced.Count;
            if (ordinary >= TagRules.MaxTagsPerMod)
                throw new InvalidDataException($"The mod has {TagRules.MaxTagsPerMod} tags already, the most it can have. Remove one on its edit page first.");

            db.ModGroupTags.RemoveRange(replaced);
            db.ModGroupTags.Add(new ModGroupTag { ModGroupId = suggestion.ModGroupId, TagId = tagId });
            if (replaced.Count > 0)
                logger.LogInformation("{Tag} replaces {Replaced} on mod group {GroupId}.",
                    snapshot.NameOf(tagId), string.Join(", ", replaced.Select(t => snapshot.NameOf(t.TagId))), suggestion.ModGroupId);
        }

        // Open suggestions of tags the mod now counts as are settled too.
        var implied = snapshot.Graph.SelfAndAncestors(tagId);
        implied.Remove(tagId);
        var settled = await db.TagSuggestions
            .Where(s => s.ModGroupId == suggestion.ModGroupId && s.State == TagSuggestionState.Open && s.TagId != null
                        && implied.Contains(s.TagId.Value))
            .ToListAsync(ct);
        foreach (var other in settled)
            Decide(other, TagSuggestionState.Approved);

        Decide(suggestion, TagSuggestionState.Approved);
        logger.LogInformation("The tag {Tag} was approved for mod group {GroupId} (suggestion {SuggestionId}, score {Score}).",
            snapshot.NameOf(tagId), suggestion.ModGroupId, suggestion.Id, suggestion.Score);
    }

    private void Decide(TagSuggestion suggestion, TagSuggestionState state)
    {
        suggestion.State = state;
        suggestion.DecidedById = UserId;
        suggestion.DecidedAt = DateTime.UtcNow;
    }

    /// <summary>Sets, changes or (with 0) takes back the account's vote, and the score with it.</summary>
    private static void SetVote(TagSuggestion suggestion, int userId, int value)
    {
        var vote = suggestion.Votes.FirstOrDefault(v => v.UserId == userId);
        if (value == 0)
        {
            if (vote is not null)
                suggestion.Votes.Remove(vote);
        }
        else if (vote is null)
        {
            suggestion.Votes.Add(new TagVote { UserId = userId, Value = value });
        }
        else
        {
            vote.Value = value;
        }

        suggestion.Score = suggestion.Votes.Sum(v => v.Value);
    }

    /// <summary>Why the tag can't be suggested because the mod has it, or has a tag that includes it; null otherwise.</summary>
    private async Task<string?> ImpliedTagAsync(int groupId, int tagId, TagSnapshot snapshot, CancellationToken ct)
    {
        var current = await db.ModGroupTags.Where(t => t.ModGroupId == groupId).Select(t => t.TagId).ToListAsync(ct);
        if (current.Contains(tagId))
            return $"The mod has the tag {snapshot.NameOf(tagId)} already.";

        foreach (var own in current)
        {
            if (snapshot.Graph.SelfAndAncestors(own).Contains(tagId))
                return $"The mod counts as {snapshot.NameOf(tagId)} already: it is tagged {snapshot.NameOf(own)}.";
        }
        return null;
    }

    /// <summary>Suggestions and new tags the account started in the last 24 hours.</summary>
    private async Task<int> StartedTodayAsync(int userId, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-1);
        var suggested = await db.TagSuggestions.CountAsync(s => s.SuggestedById == userId && s.CreatedAt >= since, ct);
        // A tag made for a mod counts through its suggestion already; one from the tags page has none.
        var created = await db.Tags.CountAsync(t => t.ProposedById == userId && t.CreatedAt >= since
            && !db.TagSuggestions.Any(s => s.TagId == t.Id && s.SuggestedById == userId && s.RequestedName != null), ct);
        return suggested + created;
    }

    private async Task<string> LabelAsync(TagSuggestion suggestion, CancellationToken ct) =>
        suggestion.TagId is { } id ? (await index.GetAsync(db, ct)).NameOf(id) : suggestion.RequestedName ?? "?";

    private async Task<Dictionary<int, string>> UserNamesAsync(IEnumerable<int?> ids, CancellationToken ct)
    {
        var wanted = ids.OfType<int>().Distinct().ToList();
        return wanted.Count == 0
            ? []
            : await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.UserName ?? "?", ct);
    }

    private async Task<TagSuggestionDto> DtoAsync(TagSuggestion suggestion, CancellationToken ct)
    {
        var threshold = await settings.TagSuggestionThresholdAsync(ct);
        var names = await UserNamesAsync([suggestion.SuggestedById], ct);
        return ToDto(suggestion, threshold, UserId, names);
    }

    private static TagSuggestionDto ToDto(TagSuggestion s, int threshold, int? userId, Dictionary<int, string> names) =>
        Fill(new TagSuggestionDto(), s, threshold, userId, names);

    private static T Fill<T>(T dto, TagSuggestion s, int threshold, int? userId, Dictionary<int, string> names) where T : TagSuggestionDto
    {
        dto.Id = s.Id;
        dto.ModGroupId = s.ModGroupId;
        dto.TagId = s.TagId;
        dto.RequestedName = s.RequestedName;
        dto.Reason = s.Reason;
        dto.State = s.State;
        dto.Score = s.Score;
        dto.Up = s.Votes.Count(v => v.Value > 0);
        dto.Down = s.Votes.Count(v => v.Value < 0);
        dto.MyVote = userId is { } u ? s.Votes.FirstOrDefault(v => v.UserId == u)?.Value ?? 0 : 0;
        dto.Counts = s.State == TagSuggestionState.Open && s.TagId is not null && s.Score >= threshold;
        dto.SuggestedBy = s.SuggestedById is { } by ? names.GetValueOrDefault(by) : null;
        dto.CreatedAt = s.CreatedAt;
        return dto;
    }
}
