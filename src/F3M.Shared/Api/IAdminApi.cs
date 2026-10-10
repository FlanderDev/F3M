using F3M.Shared.Models;
using FlanderDev.RouteGen.Abstractions;

namespace F3M.Shared.Api;

/// <summary>
/// Wire contract for admin-only user management. RouteGen generates AdminApiControllerBase
/// (server) and HttpAdminApi (client) from this. AdminService implements this directly for the
/// same reason ProfileService does — RouteGen only reads the interface's attributes, it doesn't
/// care what else implements the plain shape.
/// </summary>
[ApiRoute("api/admin", HttpClientName = Configuration.AppName)]
[GenAuthorize(Roles = AppRoles.Admin)]
public interface IAdminApi
{
    [Get("users")]
    Task<List<AdminUserDto>> GetUsersAsync(CancellationToken ct = default);

    /// <summary>Toggles the Admin role for the given user. Throws
    /// <see cref="InvalidOperationException"/> if the caller targets their own account, or
    /// <see cref="KeyNotFoundException"/> if no such user exists.</summary>
    [Post("users/{id:int}")]
    Task<AdminUserDto> ToggleAdminAsync(int id, CancellationToken ct = default);

    /// <summary>Deletes the given user's account. Same exceptions as <see cref="ToggleAdminAsync"/>.</summary>
    [Delete("users/{id:int}")]
    Task DeleteUserAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Rebuilds the whole signed catalog: re-signs every approved version, removes documents of versions that are
    /// gone, fills in file details for older uploads and rewrites the index. Safe to run at any time.
    /// </summary>
    [Post("catalog/rebuild")]
    Task<CatalogRebuildResult> RebuildCatalogAsync(CancellationToken ct = default);

    /// <summary>
    /// Gives a mod group to another user, or makes it unclaimed again (null user) so it goes to its F95 uploader on
    /// their next sign-in. Throws <see cref="KeyNotFoundException"/> for an unknown group or user.
    /// </summary>
    [Put("mods/{groupId:int}/owner")]
    Task<ModGroup> AssignModOwnerAsync(int groupId, [Body] AssignModOwnerDto dto, CancellationToken ct = default);
}
