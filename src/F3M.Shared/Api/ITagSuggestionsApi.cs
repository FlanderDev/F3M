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

    /// <summary>Suggests a tag, or requests a new one. One that is open already gets the visitor's vote instead.</summary>
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

    // ── Admins ──

    /// <summary>The queue, newest first; <paramref name="state"/> null for every state.</summary>
    [Get("admin")]
    [GenAuthorize(Roles = AppRoles.Admin)]
    Task<List<TagSuggestionAdminDto>> GetQueue([Query] TagSuggestionState? state = TagSuggestionState.Open, CancellationToken ct = default);

    /// <summary>Open suggestions and requests, for the badge in the admin sidebar.</summary>
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

    [Get("admin/limits")]
    [GenAuthorize(Roles = AppRoles.Admin)]
    Task<TagSuggestionLimits> GetLimits(CancellationToken ct = default);

    [Put("admin/limits")]
    [GenAuthorize(Roles = AppRoles.Admin)]
    Task<TagSuggestionLimits> SetLimits([Body] TagSuggestionLimits limits, CancellationToken ct = default);
}
