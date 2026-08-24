using F3M.Shared.Api;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace F3M.Server.Controllers;

/// <summary>
/// Thin controller over the RouteGen-generated AdminApiControllerBase — no route attributes,
/// no [Authorize(Roles = ...)], no route strings anywhere here; all of that comes from the
/// generated base (see obj/**/generated/RouteGen.Generators/.../Server_IAdminApi.g.cs after
/// build), which is itself derived from the attributes on IAdminApi. AdminService does the
/// actual work; this class's only job is bridging AdminService's thrown exceptions to
/// ActionResult<T>/status codes.
/// </summary>
public class AdminController(IAdminApi adminApi) : AdminApiControllerBase
{
    public override async Task<ActionResult<List<AdminUserDto>>> GetUsersAsync(CancellationToken ct)
        => Ok(await adminApi.GetUsersAsync(ct));

    public override async Task<ActionResult<AdminUserDto>> ToggleAdminAsync(int id, CancellationToken ct)
    {
        try
        {
            return Ok(await adminApi.ToggleAdminAsync(id, ct));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    public override async Task<IActionResult> DeleteUserAsync(int id, CancellationToken ct)
    {
        try
        {
            await adminApi.DeleteUserAsync(id, ct);
            return NoContent();
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }
}
