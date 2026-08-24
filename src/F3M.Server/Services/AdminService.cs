using F3M.Server.Data;
using F3M.Server.Helpers;
using F3M.Server.Models;
using F3M.Shared;
using F3M.Shared.Api;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace F3M.Server.Services;

/// <summary>
/// Server-side implementation of <see cref="IAdminApi"/>. AdminController is a thin adapter
/// over this — the [Authorize(Roles = Admin)] enforcement itself happens declaratively via the
/// generated AdminApiControllerBase, before any of this code runs; the self-action guards here
/// (can't demote/delete yourself) are business rules, not authorization, so they stay here.
/// </summary>
public class AdminService(
    AppDbContext db,
    UserManager<AppUser> userManager,
    IHttpContextAccessor httpContextAccessor,
    ILogger<AdminService> logger) : IAdminApi
{
    private ClaimsPrincipal CurrentPrincipal =>
        httpContextAccessor.HttpContext?.User
        ?? throw new InvalidOperationException("AdminService was called outside of an HTTP request.");

    public async Task<List<AdminUserDto>> GetUsersAsync(CancellationToken ct = default)
    {
        var users = await db.Users.ToListAsync(ct);
        var modCounts = await db.ModGroups
            .GroupBy(g => g.OwnerId)
            .Select(g => new { OwnerId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // Single joined query for every user's roles instead of one GetRolesAsync
        // round-trip per user (was N+1 — noticeable once the user list grows).
        var rolesByUserId = (await db.UserRoles
                .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, RoleName = r.Name })
                .ToListAsync(ct))
            .GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.RoleName ?? string.Empty).ToList());

        var result = new List<AdminUserDto>();
        foreach (var u in users)
        {
            var roles = rolesByUserId.GetValueOrDefault(u.Id, []);
            result.Add(new AdminUserDto
            {
                Id = u.Id,
                Username = u.UserName ?? string.Empty,
                Email = u.Email ?? string.Empty,
                IsAdmin = roles.Contains(AppRoles.Admin),
                Roles = roles,
                RegisteredAt = u.RegisteredAt,
                ModCount = modCounts.FirstOrDefault(m => m.OwnerId == u.Id)?.Count ?? 0
            });
        }

        return [.. result.OrderBy(u => u.Username)];
    }

    public async Task<AdminUserDto> ToggleAdminAsync(int id, CancellationToken ct = default)
    {
        var callerId = Helper.GetUserId(CurrentPrincipal)
                       ?? throw new UnauthorizedAccessException("No authenticated user.");

        if (callerId == id)
            throw new InvalidOperationException("You cannot change your own admin status.");

        var user = await userManager.FindByIdAsync(id.ToString())
                   ?? throw new KeyNotFoundException($"No user with id {id} was found.");

        var isAdmin = await userManager.IsInRoleAsync(user, AppRoles.Admin);
        if (isAdmin)
            await userManager.RemoveFromRoleAsync(user, AppRoles.Admin);
        else
            await userManager.AddToRoleAsync(user, AppRoles.Admin);

        isAdmin = !isAdmin;
        logger.LogInformation("Admin {Caller} toggled admin={IsAdmin} for user {Username}", callerId, isAdmin, user.UserName);

        var modCount = await db.ModGroups.CountAsync(g => g.OwnerId == user.Id, ct);
        var roles = await userManager.GetRolesAsync(user);
        return new AdminUserDto
        {
            Id = user.Id,
            Username = user.UserName ?? string.Empty,
            Email = user.Email ?? string.Empty,
            IsAdmin = isAdmin,
            Roles = [.. roles],
            RegisteredAt = user.RegisteredAt,
            ModCount = modCount
        };
    }

    public async Task DeleteUserAsync(int id, CancellationToken ct = default)
    {
        var callerId = Helper.GetUserId(CurrentPrincipal)
                       ?? throw new UnauthorizedAccessException("No authenticated user.");

        if (callerId == id)
            throw new InvalidOperationException("You cannot delete your own account.");

        var user = await userManager.FindByIdAsync(id.ToString())
                   ?? throw new KeyNotFoundException($"No user with id {id} was found.");

        await userManager.DeleteAsync(user);

        logger.LogInformation("Admin {Caller} deleted user {Username}", callerId, user.UserName);
    }
}
