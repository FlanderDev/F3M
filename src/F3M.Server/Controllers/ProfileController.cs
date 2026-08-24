using F3M.Shared.Api;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace F3M.Server.Controllers;

/// <summary>
/// Thin controller over the RouteGen-generated ProfileApiControllerBase — no route attributes,
/// no route strings, no [FromBody]/[Authorize] anywhere here; all of that comes from the
/// generated base (see obj/**/generated/RouteGen.Generators/.../Server_IProfileApi.g.cs after
/// build), which is itself derived from the attributes on IProfileApi. ProfileService does the
/// actual work; this class's only job is bridging ProfileService's plain return values/thrown
/// exceptions to ActionResult<T>/status codes.
/// </summary>
public class ProfileController(IProfileApi profileApi) : ProfileApiControllerBase
{
    public override async Task<ActionResult<ProfileDto>> GetProfileAsync(CancellationToken ct)
    {
        try
        {
            return Ok(await profileApi.GetProfileAsync(ct));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }

    /// <summary>The caller's own uploads (latest version per group) — including unapproved ones,
    /// since it's their own content, unlike the public browse list.</summary>
    public override async Task<ActionResult<List<Mod>>> GetMyModsAsync(CancellationToken ct)
    {
        try
        {
            return Ok(await profileApi.GetMyModsAsync(ct));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }

    /// <summary>Public view of another user's account — anyone can view this, logged in or not.</summary>
    public override async Task<ActionResult<PublicProfileDto>> GetPublicProfileAsync(string username, CancellationToken ct)
    {
        try
        {
            return Ok(await profileApi.GetPublicProfileAsync(username, ct));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    public override async Task<ActionResult<ApiResult>> ChangePasswordAsync(ChangePasswordDto dto, CancellationToken ct)
    {
        try
        {
            // Wrong-current-password etc. is an expected outcome, not an exceptional one — always
            // 200 with the ApiResult body either way, so the generated client returns a value
            // instead of throwing ApiException, matching how callers (Profile.razor.cs) already
            // check result.Success/result.Error.
            return Ok(await profileApi.ChangePasswordAsync(dto, ct));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }
}
