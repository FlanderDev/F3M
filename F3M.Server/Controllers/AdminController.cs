using System.Security.Claims;
using F3M.Server.Data;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace F3M.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Roles = "Admin")]
public class AdminController(AppDbContext db, ILogger<AdminController> logger) : ControllerBase
{
    // GET /api/admin/users
    [HttpGet("users")]
    public async Task<ActionResult<List<AdminUserDto>>> GetUsers()
    {
        var users = await db.Users.ToListAsync();
        var modCounts = await db.ModGroups
            .GroupBy(g => g.OwnerId)
            .Select(g => new { OwnerId = g.Key, Count = g.Count() })
            .ToListAsync();

        var result = users.Select(u => new AdminUserDto
        {
            Id = u.Id,
            Username = u.Username,
            Email = u.Email,
            IsAdmin = u.IsAdmin,
            RegisteredAt = u.RegisteredAt,
            ModCount = modCounts.FirstOrDefault(m => m.OwnerId == u.Id)?.Count ?? 0
        }).OrderBy(u => u.Username).ToList();

        return Ok(result);
    }

    // POST /api/admin/users/{id}/toggle-admin
    [HttpPost("users/{id:int}/toggle-admin")]
    public async Task<ActionResult<AdminUserDto>> ToggleAdmin(int id)
    {
        var callerIdStr = User.FindFirstValue("sub");
        if (!int.TryParse(callerIdStr, out var callerId))
            return Unauthorized();

        if (callerId == id)
            return BadRequest("You cannot change your own admin status.");

        var user = await db.Users.FindAsync(id);
        if (user is null)
            return NotFound();

        user.IsAdmin = !user.IsAdmin;
        await db.SaveChangesAsync();

        logger.LogInformation("Admin {Caller} toggled admin={IsAdmin} for user {Username}", callerId, user.IsAdmin, user.Username);

        var modCount = await db.ModGroups.CountAsync(g => g.OwnerId == user.Id);
        return Ok(new AdminUserDto
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email,
            IsAdmin = user.IsAdmin,
            RegisteredAt = user.RegisteredAt,
            ModCount = modCount
        });
    }

    // DELETE /api/admin/users/{id}  — remove a user account
    [HttpDelete("users/{id:int}")]
    public async Task<IActionResult> DeleteUser(int id)
    {
        var callerIdStr = User.FindFirstValue("sub");
        if (!int.TryParse(callerIdStr, out var callerId))
            return Unauthorized();

        if (callerId == id)
            return BadRequest("You cannot delete your own account.");

        var user = await db.Users.FindAsync(id);
        if (user is null)
            return NotFound();

        db.Users.Remove(user);
        await db.SaveChangesAsync();

        logger.LogInformation("Admin {Caller} deleted user {Username}", callerId, user.Username);
        return NoContent();
    }
}
