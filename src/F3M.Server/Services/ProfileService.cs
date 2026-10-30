using F3M.Server.Data;
using F3M.Server.Helpers;
using F3M.Server.Models;
using F3M.Shared.Api;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace F3M.Server.Services;

/// <summary>
/// Server-side implementation of <see cref="IProfileApi"/>. ProfileController is now just a
/// thin HTTP adapter over this — auth/claims resolution and all the actual EF/UserManager work
/// live here, so it's usable directly (e.g. from other services or tests) without going through
/// a controller action at all.
/// </summary>
public class ProfileService(
    AppDbContext db,
    UserManager<AppUser> userManager,
    SignInManager<AppUser> signInManager,
    IHttpContextAccessor httpContextAccessor,
    ILogger<ProfileService> logger) : IProfileApi
{
    private ClaimsPrincipal CurrentPrincipal =>
        httpContextAccessor.HttpContext?.User
        ?? throw new InvalidOperationException("ProfileService was called outside of an HTTP request.");

    public async Task<ProfileDto> GetProfileAsync(CancellationToken ct = default)
    {
        var user = await GetCurrentUserAsync()
                   ?? throw new UnauthorizedAccessException("No authenticated user.");

        var roles = await userManager.GetRolesAsync(user);
        var modCount = await db.ModGroups.CountAsync(g => g.OwnerId == user.Id, ct);

        return new ProfileDto
        {
            Username = user.UserName ?? string.Empty,
            Email = user.Email ?? string.Empty,
            IsEmailConfirmed = user.EmailConfirmed,
            F95Username = user.F95Username,
            F95UserId = user.F95UserId,
            RegisteredAt = user.RegisteredAt,
            Roles = [.. roles],
            ModCount = modCount
        };
    }

    public async Task<List<Mod>> GetMyModsAsync(CancellationToken ct = default)
    {
        var userId = Helper.GetUserId(CurrentPrincipal)
                     ?? throw new UnauthorizedAccessException("No authenticated user.");

        var groupIds = db.ModGroups.Where(g => g.OwnerId == userId).Select(g => g.Id);

        var latestIds = db.Mods
            .Where(m => groupIds.Contains(m.ModGroupId))
            .GroupBy(m => m.ModGroupId)
            .Select(g => g.OrderByDescending(m => m.UploadedAt).First().Id);

        return await db.Mods
            .Where(m => latestIds.Contains(m.Id))
            .Include(m => m.Files)
            .OrderByDescending(m => m.UploadedAt)
            .ToListAsync(ct);
    }

    public async Task<PublicProfileDto> GetPublicProfileAsync(string username, CancellationToken ct = default)
    {
        var user = await userManager.FindByNameAsync(username);
        if (user is null)
            return await GetAuthorWithoutAccountAsync(username, ct);

        var groupIds = db.ModGroups.Where(g => g.OwnerId == user.Id).Select(g => g.Id);

        // Only approved mods are shown publicly — unlike GetMyModsAsync, which includes the
        // owner's own unapproved uploads.
        var latestIds = db.Mods
            .Where(m => groupIds.Contains(m.ModGroupId) && m.IsApproved)
            .GroupBy(m => m.ModGroupId)
            .Select(g => g.OrderByDescending(m => m.UploadedAt).First().Id);

        var mods = await db.Mods
            .Where(m => latestIds.Contains(m.Id))
            .Include(m => m.Files)
            .OrderByDescending(m => m.UploadedAt)
            .ToListAsync(ct);

        return new PublicProfileDto
        {
            Username = user.UserName ?? username,
            RegisteredAt = user.RegisteredAt,
            ModCount = mods.Count,
            Mods = mods
        };
    }

    /// <summary>
    /// Mod cards link every author name to a profile, but authors of imported mods have no account until they claim
    /// them. Their page lists the mods published under the name instead of ending in "not found".
    /// </summary>
    private async Task<PublicProfileDto> GetAuthorWithoutAccountAsync(string author, CancellationToken ct)
    {
        var name = author.Trim().ToLower();
        var mods = await db.Mods
            .Where(m => m.IsApproved && m.IsLatestVersion && m.Author.ToLower() == name)
            .Include(m => m.Files)
            .OrderByDescending(m => m.UploadedAt)
            .ToListAsync(ct);

        if (mods.Count == 0)
            throw new KeyNotFoundException($"No user or author named '{author}' was found.");

        return new PublicProfileDto
        {
            Username = mods[0].Author,
            HasAccount = false,
            ModCount = mods.Count,
            Mods = mods
        };
    }

    public async Task<ApiResult> ChangePasswordAsync(ChangePasswordDto dto, CancellationToken ct = default)
    {
        var user = await GetCurrentUserAsync()
                   ?? throw new UnauthorizedAccessException("No authenticated user.");

        var result = await userManager.ChangePasswordAsync(user, dto.CurrentPassword, dto.NewPassword);
        if (!result.Succeeded)
            return ApiResult.Fail(string.Join(" ", result.Errors.Select(e => e.Description)));

        // A new password signs out every session, so give this one a fresh cookie.
        await signInManager.RefreshSignInAsync(user);
        logger.LogInformation("User '{Username}' changed their password.", user.UserName);
        return ApiResult.Ok();
    }

    private async Task<AppUser?> GetCurrentUserAsync()
    {
        var userId = Helper.GetUserId(CurrentPrincipal);
        return userId is null ? null : await userManager.FindByIdAsync(userId.Value.ToString());
    }
}
