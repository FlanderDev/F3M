using F3M.Server.Data;
using F3M.Server.Models;
using F3M.Shared;
using F3M.Shared.Api;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace F3M.Server.Services;

/// <summary>
/// Server-side implementation of <see cref="IF95LinkApi"/>. F95LinkController is a thin adapter
/// over this. Every outcome (pending/verified/expired/error/not-found) is modeled as a normal
/// return value via the response DTOs' Status/Success fields, never as a thrown exception — see
/// the doc comment on IF95LinkApi for why. That also means, unlike ProfileService/AdminService,
/// there's nothing here for the controller to catch: every code path returns a fully-formed DTO.
/// </summary>
public class F95LinkService(
    AppDbContext db,
    F95Service f95,
    UserManager<AppUser> userManager,
    SignInManager<AppUser> signInManager,
    ModOwnershipService ownership,
    ILogger<F95LinkService> logger) : IF95LinkApi
{
    private static readonly TimeSpan ExpiryWindow = TimeSpan.FromHours(24);

    public async Task<LinkF95StartResponse> Start(LinkF95StartRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ProfileUrl))
            return new LinkF95StartResponse { Success = false, Error = "Profile URL is required." };

        if (!F95Profile.TryParse(request.ProfileUrl, out var f95Username, out var f95UserId))
            return new LinkF95StartResponse
            {
                Success = false,
                Error = $"That is not an F95zone profile URL. It should look like {F95Profile.ExampleUrl}"
            };

        // A new account is named exactly like the F95 profile. If another F3M account already has that name, an admin
        // has to sort it out; no code is issued, so the user doesn't post one for nothing.
        var existingUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.F95UserId == f95UserId, ct);
        if (existingUser is null && await IsUsernameTakenAsync(f95Username, ct))
            return new LinkF95StartResponse
            {
                Success = false,
                UsernameTaken = true,
                F95Username = f95Username,
                Error = $"The name {f95Username} is already used by another F3M account, so your account can't be created automatically."
            };

        // Other pending verifications for this F95 user are left alone: anyone can start one for any profile, so
        // cancelling them here would let a stranger interrupt the real owner. Each one is bound to its own token.
        var guid = Guid.NewGuid().ToString("D").ToUpper();
        var clientToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var verification = new F95PendingVerification
        {
            F95UserId = f95UserId,
            F95Username = f95Username,
            VerificationGuid = guid,
            ClientTokenHash = HashToken(clientToken),
            CreatedAt = DateTime.UtcNow,
            Status = F95VerificationStatus.Pending
        };

        db.F95PendingVerifications.Add(verification);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Started F95 verification for {Username} ({UserId}).", f95Username, f95UserId);

        // Tell the user up front which account they will end up in.
        return new LinkF95StartResponse
        {
            Success = true,
            VerificationGuid = guid,
            F95UserId = f95UserId,
            F95Username = f95Username,
            ClientToken = clientToken,
            Username = existingUser?.UserName ?? f95Username,
            ExistingAccount = existingUser is not null
        };
    }

    public async Task<LinkF95PollResponse> Check(string f95UserId, LinkF95PollRequest request, CancellationToken ct = default)
    {
        // Only the browser that started a verification may finish it: the GUID is public once posted, the token is not.
        var tokenHash = HashToken(request.ClientToken);
        var verification = await db.F95PendingVerifications
            .Where(v => v.F95UserId == f95UserId &&
                        v.ClientTokenHash == tokenHash &&
                        v.Status == F95VerificationStatus.Pending)
            .FirstOrDefaultAsync(ct);

        if (verification is null)
            return new LinkF95PollResponse
            {
                Status = VerificationState.NotFound,
                Message = "This verification is no longer active. Please start again."
            };

        if (DateTime.UtcNow - verification.CreatedAt > ExpiryWindow)
        {
            verification.Status = F95VerificationStatus.Expired;
            await db.SaveChangesAsync(ct);
            return new LinkF95PollResponse
            {
                Status = VerificationState.Expired,
                Message = "The verification code expired. Please start a new verification."
            };
        }

        List<(long PostId, string Text)> posts;
        try
        {
            posts = await f95.GetProfilePostsAsync(verification.F95Username, f95UserId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to fetch profile wall for verification {Id}.", verification.Id);
            return new LinkF95PollResponse
            {
                Status = VerificationState.Error,
                Message = "Could not reach F95zone. Please try again in a moment."
            };
        }

        var (postId, text) = posts.FirstOrDefault(p =>
            p.Text.Contains(verification.VerificationGuid, StringComparison.OrdinalIgnoreCase));

        if (text is null)
            return new LinkF95PollResponse
            {
                Status = VerificationState.Pending,
                Message = "Post not found yet. Make sure you posted the code on your own profile wall."
            };

        // GUID matched — find or create the F3M account.
        var user = await db.Users.FirstOrDefaultAsync(u => u.F95UserId == f95UserId, ct);

        // Record which post matched, for audit purposes.
        verification.ProfilePostId = postId;

        bool isNewUser = user is null;

        if (user is null)
        {
            // New user — named exactly like the F95 profile. Start already checked the name, but another account may
            // have taken it since.
            var username = verification.F95Username;
            if (await IsUsernameTakenAsync(username, ct))
                return new LinkF95PollResponse
                {
                    Status = VerificationState.Error,
                    Message = $"The name {username} is already used by another F3M account. Please contact an admin."
                };

            user = new AppUser
            {
                UserName = username,
                Email = string.Empty,
                F95UserId = f95UserId,
                F95Username = verification.F95Username,
                RegisteredAt = DateTime.UtcNow
            };

            var createResult = await userManager.CreateAsync(user, request.Password);
            if (!createResult.Succeeded)
                return new LinkF95PollResponse
                {
                    Status = VerificationState.Error,
                    Message = string.Join(" ", createResult.Errors.Select(e => e.Description))
                };

            await userManager.AddToRoleAsync(user, AppRoles.User);

            logger.LogInformation("Created new F3M account '{Username}' linked to F95 user {F95UserId}.", username, f95UserId);
        }
        else
        {
            // Returning user re-linking — reset their password via Identity's own flow
            // (remove + re-add, since we don't have their old password to hand to
            // ChangePasswordAsync — proof of F95 ownership stands in for that here).
            if (await userManager.HasPasswordAsync(user))
                await userManager.RemovePasswordAsync(user);

            var addPasswordResult = await userManager.AddPasswordAsync(user, request.Password);
            if (!addPasswordResult.Succeeded)
                return new LinkF95PollResponse
                {
                    Status = VerificationState.Error,
                    Message = string.Join(" ", addPasswordResult.Errors.Select(e => e.Description))
                };

            logger.LogInformation("Existing F3M account '{Username}' re-linked via F95 and password updated.", user.UserName);
        }

        verification.Status = F95VerificationStatus.Verified;

        // Any other verifications still open for this F95 user are now pointless.
        var others = await db.F95PendingVerifications
            .Where(v => v.F95UserId == f95UserId && v.Status == F95VerificationStatus.Pending && v.Id != verification.Id)
            .ToListAsync(ct);
        foreach (var other in others)
            other.Status = F95VerificationStatus.Cancelled;

        await db.SaveChangesAsync(ct);

        // Mods imported from this F95 account become the user's, on every sign-in: some may have been imported after
        // the account was created.
        await ownership.ClaimAllAsync(user, ct);

        // Issue the auth cookie — this endpoint is itself a login (or registration) path, just
        // proven via F95 ownership instead of a password the client already knows.
        await signInManager.SignInAsync(user, isPersistent: true);

        return new LinkF95PollResponse
        {
            Status = VerificationState.Verified,
            Message = isNewUser
                ? "Account created and linked successfully."
                : "Signed in, and your password was updated.",
            Username = user.UserName,
            IsNewUser = isNewUser
        };
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // ── Helpers ───────────────────────────────────────────────────────────────
    /// <summary>Case-insensitive, like sign-in: "Name" and "name" are the same account name.</summary>
    private async Task<bool> IsUsernameTakenAsync(string username, CancellationToken ct)
    {
        var normalized = userManager.NormalizeName(username);
        return await db.Users.AnyAsync(u => u.NormalizedUserName == normalized, ct);
    }
}
