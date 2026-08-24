using F3M.Shared;
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
[Authorize(Roles = AppRoles.Admin)]
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
}
