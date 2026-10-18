using F3M.Shared.Models;
using FlanderDev.RouteGen.Abstractions;

namespace F3M.Shared.Api;

/// <summary>
/// Wire contract for tags. RouteGen generates TagsApiControllerBase and HttpTagsApi from it. Everyone can read the
/// tags; only admins change them. Changes that would break the tag tree (a loop, a chain too long, a name used twice)
/// are refused with 400 and the reason; a missing tag is 404.
/// </summary>
[ApiRoute("api/tags", HttpClientName = Configuration.AppName)]
public interface ITagsApi
{
    /// <summary>Every tag, by name.</summary>
    [Get]
    Task<List<TagDto>> GetTags(CancellationToken ct = default);

    [Post]
    [GenAuthorize(Roles = AppRoles.Admin)]
    Task<TagDto> Create([Body] TagEditDto dto, CancellationToken ct = default);

    [Put("{id:int}")]
    [GenAuthorize(Roles = AppRoles.Admin)]
    Task<TagDto> Update(int id, [Body] TagEditDto dto, CancellationToken ct = default);

    /// <summary>Removes the tag from every mod and from the tree. The tags it included stay, under its parents no longer.</summary>
    [Delete("{id:int}")]
    [GenAuthorize(Roles = AppRoles.Admin)]
    Task Delete(int id, CancellationToken ct = default);

    /// <summary>
    /// Folds tag <paramref name="id"/> into <paramref name="targetId"/>: its mods get the target instead, what it
    /// includes and what includes it moves to the target, and it is deleted. For duplicates and typos.
    /// </summary>
    [Post("{id:int}/merge/{targetId:int}")]
    [GenAuthorize(Roles = AppRoles.Admin)]
    Task<TagDto> Merge(int id, int targetId, CancellationToken ct = default);
}
