using F3M.Server.Data;
using F3M.Server.Models;
using F3M.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace F3M.Server.Services;

/// <summary>
/// Moves imported mods to their F95zone uploader, and lets admins reassign mods.
/// An imported mod (owner <see cref="ModGroup.UnclaimedOwnerId"/>) belongs to whoever has the F95 user id it was
/// imported with. That id is proven by the F95 sign-in, so claiming needs no further check; names are never used.
/// </summary>
public sealed class ModOwnershipService(AppDbContext db, ILogger<ModOwnershipService> logger)
{
    /// <summary>Hands every unclaimed mod imported for this user's F95 account to the user. Returns the group ids.</summary>
    public async Task<List<int>> ClaimAllAsync(AppUser user, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(user.F95UserId))
            return [];

        var groups = await db.ModGroups
            .Where(g => g.OwnerId == ModGroup.UnclaimedOwnerId && g.F95OwnerUserId == user.F95UserId)
            .ToListAsync(ct);

        await TakeOverAsync(groups, user, ct);
        return [.. groups.Select(g => g.Id)];
    }

    /// <summary>The "This is mine" button: claims one imported mod if the user is its F95 uploader.</summary>
    public async Task<ClaimModResult> ClaimAsync(int groupId, AppUser user, CancellationToken ct = default)
    {
        var group = await db.ModGroups.FindAsync([groupId], ct)
                    ?? throw new KeyNotFoundException($"No mod group with id {groupId} was found.");

        if (group.OwnerId == user.Id)
            return new ClaimModResult(true, "This mod is already yours.");
        if (!group.IsUnclaimed)
            return new ClaimModResult(false, "This mod already belongs to someone.");
        if (string.IsNullOrEmpty(group.F95OwnerUserId))
            return new ClaimModResult(false, "This mod has no F95zone uploader on record. Please contact an admin.");
        if (string.IsNullOrEmpty(user.F95UserId))
            return new ClaimModResult(false, "Your account is not linked to F95zone. Sign in through F95zone to claim mods you uploaded there.");
        if (group.F95OwnerUserId != user.F95UserId)
        {
            var uploader = group.F95OwnerName is { Length: > 0 } name ? $"by {name} " : string.Empty;
            return new ClaimModResult(false,
                $"This mod was posted on F95zone {uploader}from a different account than yours. If it is yours, please contact an admin.");
        }

        await TakeOverAsync([group], user, ct);
        return new ClaimModResult(true, "The mod is now yours. You can edit it and upload new versions.");
    }

    /// <summary>
    /// Admin: gives a mod to another user, or back to "unclaimed" when <paramref name="userId"/> is null
    /// (it then goes to its F95 uploader on their next sign-in).
    /// </summary>
    public async Task<ModGroup> AssignAsync(int groupId, int? userId, CancellationToken ct = default)
    {
        var group = await db.ModGroups.FindAsync([groupId], ct)
                    ?? throw new KeyNotFoundException($"No mod group with id {groupId} was found.");

        AppUser? user = null;
        if (userId is { } id)
            user = await db.Users.FindAsync([id], ct) ?? throw new KeyNotFoundException($"No user with id {id} was found.");

        var previousOwner = group.OwnerId;
        if (user is not null)
        {
            await TakeOverAsync([group], user, ct);
        }
        else
        {
            group.OwnerId = ModGroup.UnclaimedOwnerId;
            group.ClaimedAt = null;
            await db.Mods.Where(m => m.ModGroupId == group.Id && m.UserId == previousOwner)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.UserId, ModGroup.UnclaimedOwnerId), ct);
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("Mod group {GroupId} reassigned from user {From} to {To}.", group.Id, previousOwner, user?.Id.ToString() ?? "unclaimed");
        return group;
    }

    private async Task TakeOverAsync(IReadOnlyCollection<ModGroup> groups, AppUser user, CancellationToken ct)
    {
        if (groups.Count == 0)
            return;

        var ids = groups.Select(g => g.Id).ToList();
        var previousOwners = groups.Select(g => g.OwnerId).Distinct().ToList();
        foreach (var group in groups)
        {
            group.OwnerId = user.Id;
            group.ClaimedAt = DateTime.UtcNow;
        }

        // Versions stored for the previous owner (imported ones have the unclaimed id) count as the new owner's uploads.
        await db.Mods.Where(m => ids.Contains(m.ModGroupId) && m.UserId != null && previousOwners.Contains(m.UserId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.UserId, user.Id), ct);
        await db.SaveChangesAsync(ct);

        // The signed catalog carries the author text, not the owner, so it stays valid without republishing.
        logger.LogInformation("User {User} ({F95UserId}) took over mod groups {Groups}.", user.UserName, user.F95UserId, string.Join(", ", ids));
    }
}
