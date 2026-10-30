using F3M.Shared.Models;
using FlanderDev.RouteGen.Abstractions;

namespace F3M.Shared.Api;

/// <summary>
/// Wire contract for tag suggestions and votes. RouteGen generates TagSuggestionsApiControllerBase and
/// HttpTagSuggestionsApi from it. A refused action (a tag the mod has, the daily limit, voting on one's own mod, ...)
/// is 400 with the reason; deciding on a mod that isn't one's own is 403; a missing suggestion or mod is 404.
/// </summary>
[ApiRoute("api/tag-suggestions", HttpClientName = Configuration.AppName)]
public interface ITagSuggestionsApi
{
    /// <summary>The open suggestions of a mod, for its page.</summary>
    [Get("mod/{groupId:int}")]
    Task<ModTagSuggestions> GetForMod(int groupId, CancellationToken ct = default);

    /// <summary>
    /// Suggests a tag. With a new name it creates that tag first, as a proposed tag. A suggestion that is open already
    /// gets the visitor's vote instead.
    /// </summary>
    [Post("mod/{groupId:int}")]
    [GenAuthorize]
    Task<TagSuggestionDto> Suggest(int groupId, [Body] TagSuggestRequest request, CancellationToken ct = default);

    [Post("{id:int}/vote")]
    [GenAuthorize]
    Task<TagSuggestionDto> Vote(int id, [Body] TagVoteRequest vote, CancellationToken ct = default);

    /// <summary>The mod's owner or an admin: the tag goes on the mod. Requests are approved by creating the tag (admins).</summary>
    [Post("{id:int}/approve")]
    [GenAuthorize]
    Task<TagSuggestionDto> Approve(int id, CancellationToken ct = default);

    /// <summary>The mod's owner or an admin: hidden, and the tag can't be suggested for that mod again.</summary>
    [Post("{id:int}/reject")]
    [GenAuthorize]
    Task<TagSuggestionDto> Reject(int id, CancellationToken ct = default);

    // ── Tags users created ──

    /// <summary>Tags users created that are still being voted on, newest first. Admins also see who voted.</summary>
    [Get("proposals")]
    Task<List<TagProposalDto>> GetProposals(CancellationToken ct = default);

    /// <summary>Votes on whether a proposed tag should exist; enough points make it a regular tag.</summary>
    [Post("proposals/{tagId:int}/vote")]
    [GenAuthorize]
    Task<TagProposalDto> VoteProposal(int tagId, [Body] TagVoteRequest vote, CancellationToken ct = default);

    /// <summary>Makes a proposed tag a regular one, whatever its votes.</summary>
    [Post("proposals/{tagId:int}/accept")]
    [GenAuthorize(Roles = AppRoles.Admin)]
    Task<TagProposalDto> AcceptProposal(int tagId, CancellationToken ct = default);

    /// <summary>Deletes a proposed tag, and with it its place on mods and its suggestions.</summary>
    [Delete("proposals/{tagId:int}")]
    [GenAuthorize(Roles = AppRoles.Admin)]
    Task RejectProposal(int tagId, CancellationToken ct = default);

    // ── Admins ──

    /// <summary>The queue, newest first; <paramref name="state"/> null for every state.</summary>
    [Get("admin")]
    [GenAuthorize(Roles = AppRoles.Admin)]
    Task<List<TagSuggestionAdminDto>> GetQueue([Query] TagSuggestionState? state = TagSuggestionState.Open, CancellationToken ct = default);

    /// <summary>Open suggestions and proposed tags, for the badge in the admin sidebar.</summary>
    [Get("admin/open-count")]
    [GenAuthorize(Roles = AppRoles.Admin)]
    Task<int> GetOpenCount(CancellationToken ct = default);

    /// <summary>
    /// Creates the tag a request asks for (with the name and place in the tree given) and turns the request, and every
    /// other open request for the same name, into a suggestion of it.
    /// </summary>
    [Post("{id:int}/create-tag")]
    [GenAuthorize(Roles = AppRoles.Admin)]
    Task<TagSuggestionDto> CreateTag(int id, [Body] TagEditDto tag, CancellationToken ct = default);

    /// <summary>Approves every open suggestion whose score reached the limit; returns how many.</summary>
    [Post("admin/approve-counting")]
    [GenAuthorize(Roles = AppRoles.Admin)]
    Task<int> ApproveCounting(CancellationToken ct = default);

    /// <summary>Deletes a rejected suggestion, so the tag can be suggested for that mod again.</summary>
    [Delete("{id:int}")]
    [GenAuthorize(Roles = AppRoles.Admin)]
    Task Delete(int id, CancellationToken ct = default);

    /// <summary>Deletes every rejected suggestion; returns how many.</summary>
    [Delete("admin/rejected")]
    [GenAuthorize(Roles = AppRoles.Admin)]
    Task<int> DeleteRejected(CancellationToken ct = default);

    [Get("admin/limits")]
    [GenAuthorize(Roles = AppRoles.Admin)]
    Task<TagSuggestionLimits> GetLimits(CancellationToken ct = default);

    [Put("admin/limits")]
    [GenAuthorize(Roles = AppRoles.Admin)]
    Task<TagSuggestionLimits> SetLimits([Body] TagSuggestionLimits limits, CancellationToken ct = default);
}
