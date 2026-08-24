using F3M.Shared;
using F3M.Shared.Models;
using FlanderDev.RouteGen.Abstractions;

namespace F3M.Shared.Api;

/// <summary>
/// Typed contract for profile operations. RouteGen generates both:
///   - Server: ProfileApiControllerBase — the thin ProfileController inherits from this.
///   - Client: HttpProfileApi — registered in DI in place of the old hand-written ProfileApiClient.
/// ProfileService (server business logic) implements this interface directly too — that's fine,
/// RouteGen only reads the interface's attributes via the semantic model; it doesn't care who
/// else implements the plain interface shape.
/// "Self" operations (own profile, own mods, change own password) take no user identifier —
/// both implementations resolve "the current user" from the ambient auth context (HttpContext
/// on the server, the auth cookie on the client), the same way the old hand-written controller did.
/// </summary>
[ApiRoute("api/profile", HttpClientName = Configuration.AppName)]
[Authorize]
public interface IProfileApi
{
    /// <summary>The currently authenticated user's own profile.</summary>
    [Get]
    Task<ProfileDto> GetProfileAsync(CancellationToken ct = default);

    /// <summary>The currently authenticated user's own uploads — includes unapproved ones,
    /// since it's their own content.</summary>
    [Get("mods")]
    Task<List<Mod>> GetMyModsAsync(CancellationToken ct = default);

    /// <summary>Public view of another user's account. Throws <see cref="KeyNotFoundException"/>
    /// (mapped to 404 by the controller) if no such user exists.</summary>
    [Get("u/{username}")]
    [AllowAnonymous]
    Task<PublicProfileDto> GetPublicProfileAsync(string username, CancellationToken ct = default);

    /// <summary>Changes the currently authenticated user's password.</summary>
    [Post("password")]
    Task<ApiResult> ChangePasswordAsync([Body] ChangePasswordDto dto, CancellationToken ct = default);
}
