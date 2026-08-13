using F3M.Server.Data;
using F3M.Server.Helpers;
using F3M.Server.Models;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace F3M.Server.Controllers;

/// <summary>
/// Self-service account endpoints for the currently logged-in user. Distinct from
/// AdminController, which manages *other* users' accounts and requires the Admin role —
/// everything here only ever reads/writes the caller's own account.
/// </summary>
[ApiController]
[Route(Endpoints.Profile.Base)]
[Authorize]
public class ProfileController(
    AppDbContext db,
    UserManager<AppUser> userManager,
    ILogger<ProfileController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ProfileDto>> Get()
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return Unauthorized();

        var roles = await userManager.GetRolesAsync(user);
        var modCount = await db.ModGroups.CountAsync(g => g.OwnerId == user.Id);

        return Ok(new ProfileDto
        {
            Username = user.UserName ?? string.Empty,
            Email = user.Email ?? string.Empty,
            IsEmailConfirmed = user.EmailConfirmed,
            F95Username = user.F95Username,
            F95UserId = user.F95UserId,
            RegisteredAt = user.RegisteredAt,
            Roles = [.. roles],
            ModCount = modCount
        });
    }

    /// <summary>The caller's own uploads (latest version per group) — including unapproved ones,
    /// since it's their own content, unlike the public browse list.</summary>
    [HttpGet(Endpoints.Profile.MyMods)]
    public async Task<ActionResult<List<Mod>>> GetMyMods()
    {
        var userId = Helper.GetUserId(User);
        if (userId is null)
            return Unauthorized();

        var groupIds = db.ModGroups.Where(g => g.OwnerId == userId).Select(g => g.Id);

        var latestIds = db.Mods
            .Where(m => groupIds.Contains(m.ModGroupId))
            .GroupBy(m => m.ModGroupId)
            .Select(g => g.OrderByDescending(m => m.UploadedAt).First().Id);

        var mods = await db.Mods
            .Where(m => latestIds.Contains(m.Id))
            .Include(m => m.Files)
            .OrderByDescending(m => m.UploadedAt)
            .ToListAsync();

        return Ok(mods);
    }

    [HttpPost(Endpoints.Profile.ChangePassword)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordDto dto)
    {
        var user = await GetCurrentUserAsync();
        if (user is null)
            return Unauthorized();

        var result = await userManager.ChangePasswordAsync(user, dto.CurrentPassword, dto.NewPassword);
        if (!result.Succeeded)
            return BadRequest(new { Error = string.Join(" ", result.Errors.Select(e => e.Description)) });

        logger.LogInformation("User '{Username}' changed their password.", user.UserName);
        return Ok();
    }

    private async Task<AppUser?> GetCurrentUserAsync()
    {
        var userId = Helper.GetUserId(User);
        return userId is null ? null : await userManager.FindByIdAsync(userId.Value.ToString());
    }
}
