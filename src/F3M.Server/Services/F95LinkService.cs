using F3M.Server.Data;
using F3M.Server.Models;
using F3M.Server.Services.Setup;
using F3M.Server.Statistics;
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
    F95BotSettings botSettings,
    UserManager<AppUser> userManager,
    SignInManager<AppUser> signInManager,
    ModOwnershipService ownership,
    IHttpContextAccessor httpContextAccessor,
    StatsRecorder stats,
    ILogger<F95LinkService> logger) : IF95LinkApi
{
    private static readonly TimeSpan ExpiryWindow = TimeSpan.FromHours(24);

    public async Task<LinkF95StartResponse> Start(LinkF95StartRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ProfileUrl))
            return new LinkF95StartResponse { Success = false, Error = "Profile URL is required." };

        // Without a bot account nobody could check the posted code; say so before the user posts one.
        if (!botSettings.IsConfigured)
            return new LinkF95StartResponse
            {
                Success = false,
                Error = "Linking F95zone profiles isn't set up on this site yet. Please try again later or contact an admin."
            };

        if (!F95Profile.TryParse(request.ProfileUrl, out var f95Username, out var f95UserId))
            return new LinkF95StartResponse
            {
                Success = false,
                Error = $"That is not an F95zone profile URL. It should look like {F95Profile.ExampleUrl}"
            };

        // A new account is named exactly like the F95 profile. If another F3M account already has that name, an admin
        // has to sort it out; no code is issued, so the user doesn't post one for nothing. An account that is already
        // linked to this profile (found by id, so renames don't matter), or the signed-in account, needs no new name.
        var existingUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.F95UserId == f95UserId, ct)
                           ?? await UnlinkedSignedInUserAsync();
        if (existingUser is null && await IsUsernameTakenAsync(f95Username, ct))
        {
            logger.LogWarning("Refused to start F95 verification for {Username} ({UserId}): the name belongs to an account " +
                              "that isn't linked to this profile.", f95Username, f95UserId);
            return new LinkF95StartResponse
            {
                Success = false,
                UsernameTaken = true,
                F95Username = f95Username,
                Error = $"The name {f95Username} is already used by another F3M account, so your account can't be created automatically."
            };
        }

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

        F95ProfilePage page;
        try
        {
            page = await f95.GetProfilePageAsync(verification.F95Username, f95UserId, ct);
        }
        catch (ProfileHiddenException)
        {
            // Not the user's mistake in the form, and easy to fix: the verification stays open for the next try.
            logger.LogInformation("Verification {Id}: {Username} hides their F95zone profile from other members.",
                verification.Id, verification.F95Username);
            return new LinkF95PollResponse
            {
                Status = VerificationState.Pending,
                ProfileHidden = true,
                Message = "Your F95zone profile is hidden from other members, so your post can't be seen. Allow members to view " +
                          "the details on your profile page in your F95zone privacy settings, then try again."
            };
        }
        catch (ProfileMismatchException ex)
        {
            logger.LogWarning("Verification {Id}: {Url} leads to {Name}.{UserId}.", verification.Id,
                F95Profile.Url(verification.F95Username, f95UserId), ex.ActualName, ex.ActualId);
            verification.Status = F95VerificationStatus.Cancelled;
            await db.SaveChangesAsync(ct);
            return new LinkF95PollResponse
            {
                Status = VerificationState.Error,
                Message = "That profile address doesn't match the F95zone account it opens. Copy the address from your own profile page and start again."
            };
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

        // Only what the profile's owner wrote counts: anyone can post on someone's wall, and the code is public once
        // posted. The author is matched by user id, which, unlike the name, is the same in the URL and on the page.
        var code = CodeDigits(verification.VerificationGuid);
        var ownPosts = page.Posts.Where(p => p.AuthorId == f95UserId).ToList();
        var match = ownPosts.FirstOrDefault(p => CodeDigits(p.Text).Contains(code, StringComparison.Ordinal));

        if (match is null)
            return CodeNotFound(verification, page, ownPosts, code);

        var postId = match.Id;

        // GUID matched — find or create the F3M account: the one linked to this profile, else the signed-in one.
        var user = await db.Users.FirstOrDefaultAsync(u => u.F95UserId == f95UserId, ct)
                   ?? await UnlinkedSignedInUserAsync();

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
            var linkedNow = user.F95UserId is null;
            user.F95UserId = f95UserId;
            user.F95Username = verification.F95Username; // Follows renames on F95zone.

            // Returning user: proof of F95 ownership stands in for the forgotten password. A reset token checks the new
            // password before anything changes, so a refused one leaves the old password in place.
            var resetToken = await userManager.GeneratePasswordResetTokenAsync(user);
            var resetResult = await userManager.ResetPasswordAsync(user, resetToken, request.Password);
            if (!resetResult.Succeeded)
                return new LinkF95PollResponse
                {
                    Status = VerificationState.Error,
                    Message = string.Join(" ", resetResult.Errors.Select(e => e.Description))
                };

            // Whoever forgot their password has often locked the account with wrong guesses; the new one works at once.
            await userManager.SetLockoutEndDateAsync(user, null);
            await userManager.ResetAccessFailedCountAsync(user);

            if (linkedNow)
                logger.LogInformation("F3M account '{Username}' linked to F95 user {F95UserId} and its password set.", user.UserName, f95UserId);
            else
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
        stats.Count(StatsRecorder.Metrics.SignInF95);

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

    /// <summary>
    /// The signed-in account when it has no F95zone profile yet (the admin account, say). Verifying then links that
    /// account instead of creating a new one under the F95 name, which may even be the account's own name.
    /// </summary>
    private async Task<AppUser?> UnlinkedSignedInUserAsync()
    {
        if (httpContextAccessor.HttpContext?.User is not { Identity.IsAuthenticated: true } principal)
            return null;

        var user = await userManager.GetUserAsync(principal);
        return user is { F95UserId: null } ? user : null;
    }

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// The hex digits of a text, upper case, everything else dropped. The code then still matches when it was posted
    /// in lower case, without its dashes, broken over lines, with invisible characters (zero-width spaces from copying)
    /// or wrapped in other text. Chance matches are no concern: the code is 32 random hex digits.
    /// </summary>
    private static string CodeDigits(string text) =>
        string.Concat(text.Where(char.IsAsciiHexDigit).Select(char.ToUpperInvariant));

    /// <summary>The code wasn't found: logs everything that explains why (a warning), and tells the user what fits.</summary>
    private LinkF95PollResponse CodeNotFound(F95PendingVerification verification, F95ProfilePage page, List<F95ProfilePost> ownPosts, string code)
    {
        var byOthers = page.Posts.Where(p => p.AuthorId != verification.F95UserId && CodeDigits(p.Text).Contains(code, StringComparison.Ordinal)).ToList();
        var elsewhereOnPage = CodeDigits(page.PageText).Contains(code, StringComparison.Ordinal);

        string reason, message;
        if (byOthers.Count > 0)
        {
            var other = byOthers[0];
            reason = $"the code is in {(other.IsComment ? "comment" : "post")} {other.Id} by another account, {other.AuthorName} ({other.AuthorId ?? "no id"})";
            message = $"The code is on your profile, but it was posted from another F95zone account ({other.AuthorName}). " +
                      $"Post it yourself, signed in to F95zone as {verification.F95Username}.";
        }
        else if (elsewhereOnPage)
        {
            reason = "the code is on the page, but not in any post the bot could read (has the page layout changed?)";
            message = DefaultPendingMessage;
        }
        else if (!page.SignedIn)
        {
            reason = "the bot saw the page signed out, even after signing in again";
            message = "F95zone didn't let the checker see your profile just now. Please try again in a minute.";
        }
        else if (page.Posts.Count == 0)
        {
            reason = "the page shows no profile posts at all (profile private, posts hidden, or the page layout changed)";
            message = "Your profile posts couldn't be read. If your F95zone privacy settings hide your profile, let members see it, then try again.";
        }
        else
        {
            reason = ownPosts.Count == 0
                ? "none of the posts on the page are by the profile's owner"
                : "none of the owner's posts contain the code (not posted yet, still awaiting approval, or cut short)";
            message = DefaultPendingMessage;
        }

        var posts = page.Posts.Count == 0
            ? "  (none)"
            : string.Join('\n', page.Posts.Take(15).Select(p =>
                $"  {(p.IsComment ? "comment" : "post")} {p.Id} by {p.AuthorName} ({p.AuthorId ?? "no id"}): {Excerpt(p.Text)}"));

        logger.LogWarning(
            "Verification {Id} for {Username} ({UserId}): the code wasn't found, {Reason}.\n" +
            "Page: {Url} (\"{Title}\", {Length} characters, signed in: {SignedIn}), {PostCount} posts and comments, {OwnCount} by the owner:\n{Posts}",
            verification.Id, verification.F95Username, verification.F95UserId, reason,
            page.FinalUri, page.Title, page.HtmlLength, page.SignedIn, page.Posts.Count, ownPosts.Count, posts);

        return new LinkF95PollResponse { Status = VerificationState.Pending, Message = message };
    }

    private const string DefaultPendingMessage =
        "No post with the code yet. Check that you posted it on your own profile and that it contains the whole code, then try again. It can take a minute to show up.";

    private static string Excerpt(string text)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length > 80 ? line[..80] + "…" : line.Length == 0 ? "(no text)" : line;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    /// <summary>Case-insensitive, like sign-in: "Name" and "name" are the same account name.</summary>
    private async Task<bool> IsUsernameTakenAsync(string username, CancellationToken ct)
    {
        var normalized = userManager.NormalizeName(username);
        return await db.Users.AnyAsync(u => u.NormalizedUserName == normalized, ct);
    }
}
