using F3M.Server.Data;
using F3M.Server.Models;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace F3M.Server.Controllers;

[ApiController]
[Route(Endpoints.Admin.Base)]
[Authorize(Roles = AppRoles.Admin)]
public class AdminController(
    AppDbContext db,
    UserManager<AppUser> userManager,
    ILogger<AdminController> logger) : ControllerBase
{
    [HttpGet(Endpoints.Users)]
    public async Task<ActionResult<List<AdminUserDto>>> GetUsers()
    {
        var users = await db.Users.ToListAsync();
        var modCounts = await db.ModGroups
            .GroupBy(g => g.OwnerId)
            .Select(g => new { OwnerId = g.Key, Count = g.Count() })
            .ToListAsync();

        var result = new List<AdminUserDto>();
        foreach (var u in users)
        {
            var roles = await userManager.GetRolesAsync(u);
            result.Add(new AdminUserDto
            {
                Id = u.Id,
                Username = u.UserName ?? string.Empty,
                Email = u.Email ?? string.Empty,
                IsAdmin = roles.Contains(AppRoles.Admin),
                Roles = [.. roles],
                RegisteredAt = u.RegisteredAt,
                ModCount = modCounts.FirstOrDefault(m => m.OwnerId == u.Id)?.Count ?? 0
            });
        }

        return Ok(result.OrderBy(u => u.Username).ToList());
    }

    [HttpPost($"{Endpoints.Users}/{{id:int}}")]
    public async Task<ActionResult<AdminUserDto>> ToggleAdmin(int id)
    {
        var callerIdStr = User.FindFirstValue("sub");
        if (!int.TryParse(callerIdStr, out var callerId))
            return Unauthorized();

        if (callerId == id)
            return BadRequest("You cannot change your own admin status.");

        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null)
            return NotFound();

        var isAdmin = await userManager.IsInRoleAsync(user, AppRoles.Admin);
        if (isAdmin)
            await userManager.RemoveFromRoleAsync(user, AppRoles.Admin);
        else
            await userManager.AddToRoleAsync(user, AppRoles.Admin);

        isAdmin = !isAdmin;
        logger.LogInformation("Admin {Caller} toggled admin={IsAdmin} for user {Username}", callerId, isAdmin, user.UserName);

        var modCount = await db.ModGroups.CountAsync(g => g.OwnerId == user.Id);
        var roles = await userManager.GetRolesAsync(user);
        return Ok(new AdminUserDto
        {
            Id = user.Id,
            Username = user.UserName ?? string.Empty,
            Email = user.Email ?? string.Empty,
            IsAdmin = isAdmin,
            Roles = [.. roles],
            RegisteredAt = user.RegisteredAt,
            ModCount = modCount
        });
    }

    [HttpDelete($"{Endpoints.Users}/{{id:int}}")]
    public async Task<IActionResult> DeleteUser(int id)
    {
        var callerIdStr = User.FindFirstValue("sub");
        if (!int.TryParse(callerIdStr, out var callerId))
            return Unauthorized();

        if (callerId == id)
            return BadRequest("You cannot delete your own account.");

        var user = await userManager.FindByIdAsync(id.ToString());
        if (user is null)
            return NotFound();

        await userManager.DeleteAsync(user);

        logger.LogInformation("Admin {Caller} deleted user {Username}", callerId, user.UserName);
        return NoContent();
    }
}
