using F3M.Server.Data;
using F3M.Server.Helpers;
using F3M.Server.Logging;
using F3M.Server.Models;
using F3M.Server.Services.Setup;
using F3M.Shared;
using F3M.Shared.Api;
using F3M.Shared.Models;
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
    LogDbContext log,
    UserManager<AppUser> userManager,
    IHttpContextAccessor httpContextAccessor,
    CatalogService catalog,
    ModOwnershipService ownership,
    AdminAccount adminAccount,
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
                F95UserId = u.F95UserId,
                F95Username = u.F95Username,
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
        if (isAdmin && adminAccount.Is(user.UserName))
            throw new InvalidOperationException(SettingsAdmin(user));

        var roleResult = isAdmin
            ? await userManager.RemoveFromRoleAsync(user, AppRoles.Admin)
            : await userManager.AddToRoleAsync(user, AppRoles.Admin);
        if (!roleResult.Succeeded)
            throw new InvalidOperationException(string.Join(" ", roleResult.Errors.Select(e => e.Description)));

        isAdmin = !isAdmin;
        logger.LogInformation("Admin {Caller} toggled admin={IsAdmin} for user {Username}", callerId, isAdmin, user.UserName);

        var modCount = await db.ModGroups.CountAsync(g => g.OwnerId == user.Id, ct);
        var roles = await userManager.GetRolesAsync(user);
        return new AdminUserDto
        {
            Id = user.Id,
            Username = user.UserName ?? string.Empty,
            F95UserId = user.F95UserId,
            F95Username = user.F95Username,
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

        if (adminAccount.Is(user.UserName))
            throw new InvalidOperationException(SettingsAdmin(user));

        // Their mods must not stay with an account that no longer exists: only admins could manage them then. Unclaimed,
        // they go back to their F95 uploader (if recorded) on that user's next sign-in, or an admin assigns them.
        var ownedGroups = await db.ModGroups.Where(g => g.OwnerId == user.Id).Select(g => g.Id).ToListAsync(ct);
        foreach (var groupId in ownedGroups)
            await ownership.AssignAsync(groupId, null, ct);

        var deleteResult = await userManager.DeleteAsync(user);
        if (!deleteResult.Succeeded)
            throw new InvalidOperationException(string.Join(" ", deleteResult.Errors.Select(e => e.Description)));

        logger.LogInformation("Admin {Caller} deleted user {Username}", callerId, user.UserName);
    }

    private static string SettingsAdmin(AppUser user) =>
        $"{user.UserName} is the admin account from the server settings (F3M_ADMIN_USERNAME). Change it there first.";

    public async Task<CatalogRebuildResult> RebuildCatalogAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Admin {Caller} started a catalog rebuild", userManager.GetUserId(CurrentPrincipal));
        return await catalog.RebuildAllAsync(ct);
    }

    public async Task<LogPage> GetLogAsync(int skip = 0, int take = 25, LogSeverity minLevel = LogSeverity.Information,
        LogSourceFilter source = LogSourceFilter.All, string? search = null, CancellationToken ct = default)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, IAdminApi.MaxLogEntriesPerPage);

        var query = log.Entries.AsNoTracking().Where(e => e.Level >= minLevel);
        if (source == LogSourceFilter.Server)
            query = query.Where(e => e.Source == LogSource.Server);
        else if (source == LogSourceFilter.Browser)
            query = query.Where(e => e.Source == LogSource.Browser);

        if (search?.Trim() is { Length: > 0 } text)
        {
            var pattern = $"%{text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
            query = query.Where(e => EF.Functions.Like(e.Message, pattern, "\\")
                                     || (e.Category != null && EF.Functions.Like(e.Category, pattern, "\\"))
                                     || (e.UserName != null && EF.Functions.Like(e.UserName, pattern, "\\")));
        }

        // Ids grow with every entry, so they order by arrival even when two share a timestamp.
        var items = await query.OrderByDescending(e => e.Id).Skip(skip).Take(take).ToListAsync(ct);
        return new LogPage { Items = items, Total = await query.CountAsync(ct) };
    }

    public async Task<ModGroup> AssignModOwnerAsync(int groupId, AssignModOwnerDto dto, CancellationToken ct = default)
    {
        logger.LogInformation("Admin {Caller} assigns mod group {GroupId} to {UserId}", userManager.GetUserId(CurrentPrincipal), groupId,
            dto.UserId?.ToString() ?? "unclaimed");
        return await ownership.AssignAsync(groupId, dto.UserId, ct);
    }
}
