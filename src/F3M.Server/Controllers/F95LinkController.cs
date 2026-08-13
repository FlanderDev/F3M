using F3M.Server.Data;
using F3M.Server.Models;
using F3M.Server.Services;
using F3M.Shared;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;

namespace F3M.Server.Controllers;

[ApiController]
[Route(Endpoints.F95Link.Base)]
public partial class F95LinkController(
    AppDbContext db,
    F95Service f95,
    IConfiguration config,
    ILogger<F95LinkController> logger) : ControllerBase
{
    // Matches: https://f95zone.to/members/username.12345/
    [GeneratedRegex(@"^https?://f95zone\.to/members/([a-zA-Z0-9_.\-]+?)\.(\d+)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex F95ProfileUrlRegex();

    private static readonly TimeSpan ExpiryWindow = TimeSpan.FromHours(24);

    // ── POST /api/auth/f95/start ──────────────────────────────────────────────

    [HttpPost(Endpoints.Start)]
    public async Task<ActionResult<LinkF95StartResponse>> Start(
        [FromBody] LinkF95StartRequest request,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ProfileUrl))
            return BadRequest(new LinkF95StartResponse
            {
                Success = false,
                Error = "Profile URL is required."
            });

        var match = F95ProfileUrlRegex().Match(request.ProfileUrl.Trim());
        if (!match.Success)
            return BadRequest(new LinkF95StartResponse
            {
                Success = false,
                Error = "Invalid F95zone profile URL. Expected format: https://f95zone.to/members/username.12345/"
            });

        var f95Username = match.Groups[1].Value;
        var f95UserId = match.Groups[2].Value;

        // Cancel any existing pending verification for this F95 user.
        var existing = await db.F95PendingVerifications
            .Where(v => v.F95UserId == f95UserId &&
                        v.Status == F95VerificationStatus.Pending)
            .ToListAsync(ct);

        foreach (var v in existing)
            v.Status = F95VerificationStatus.Cancelled;

        if (existing.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Cancelled {Count} existing pending verification(s) for F95 user {UserId}.", existing.Count, f95UserId);
        }

        var guid = Guid.NewGuid().ToString("D").ToUpper();

        var verification = new F95PendingVerification
        {
            F95UserId = f95UserId,
            F95Username = f95Username,
            VerificationGuid = guid,
            CreatedAt = DateTime.UtcNow,
            Status = F95VerificationStatus.Pending
        };

        db.F95PendingVerifications.Add(verification);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Started F95 verification for {Username} ({UserId}).", f95Username, f95UserId);

        return Ok(new LinkF95StartResponse
        {
            Success = true,
            VerificationGuid = guid,
            F95UserId = f95UserId,
            F95Username = f95Username
        });
    }

    // ── POST /api/auth/f95/check/{f95UserId} ─────────────────────────────────

    [HttpPost($"{Endpoints.Check}/{{f95UserId}}")]
    public async Task<ActionResult<LinkF95PollResponse>> Check(
        string f95UserId,
        [FromBody] LinkF95PollRequest request,
        CancellationToken ct)
    {
        var verification = await db.F95PendingVerifications
            .Where(v => v.F95UserId == f95UserId &&
                        v.Status == F95VerificationStatus.Pending)
            .OrderByDescending(v => v.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (verification is null)
            return NotFound(new LinkF95PollResponse
            {
                Status = VerificationState.NotFound,
                Message = "No pending verification found for this user. Please start again."
            });

        if (DateTime.UtcNow - verification.CreatedAt > ExpiryWindow)
        {
            verification.Status = F95VerificationStatus.Expired;
            await db.SaveChangesAsync(ct);
            return StatusCode(410, new LinkF95PollResponse
            {
                Status = VerificationState.Expired,
                Message = "The verification code expired. Please start a new verification."
            });
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
            return StatusCode(502, new LinkF95PollResponse
            {
                Status = VerificationState.Error,
                Message = "Could not reach F95zone. Please try again in a moment."
            });
        }

        var (postId, text) = posts.FirstOrDefault(p =>
            p.Text.Contains(verification.VerificationGuid, StringComparison.OrdinalIgnoreCase));

        if (text is null)
            return Ok(new LinkF95PollResponse
            {
                Status = VerificationState.Pending,
                Message = "Post not found yet. Make sure you posted the code on your own profile wall."
            });

        // GUID matched — find or create the F3M account.
        var user = await db.Users.FirstOrDefaultAsync(u => u.F95UserId == f95UserId, ct);

        // Record which post matched, for audit purposes.
        verification.ProfilePostId = postId;

        var passwordHash = AuthController.HashPassword(request.Password);

        if (user is null)
        {
            // New user — derive a unique username from the F95 username.
            var username = await ResolveUniqueUsernameAsync(verification.F95Username, f95UserId, ct);

            user = new AppUser
            {
                Username = username,
                Email = string.Empty,
                PasswordHash = passwordHash,
                F95UserId = f95UserId,
                F95Username = verification.F95Username,
                RegisteredAt = DateTime.UtcNow
            };

            db.Users.Add(user);
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Created new F3M account '{Username}' linked to F95 user {F95UserId}.", username, f95UserId);

            await ClaimModAuthorshipByNameAsync(user);
        }
        else
        {
            // Returning user re-linking — update their password.
            user.PasswordHash = passwordHash;
            logger.LogInformation("Existing F3M account '{Username}' re-linked via F95 and password updated.", user.Username);
        }

        verification.Status = F95VerificationStatus.Verified;
        await db.SaveChangesAsync(ct);

        var token = GenerateToken(user);

        return Ok(new LinkF95PollResponse
        {
            Status = VerificationState.Verified,
            Message = user.RegisteredAt == DateTime.UtcNow
                ? "Account created and linked successfully."
                : "Logged in via F95zone account.",
            Token = token,
            User = new UserInfo
            {
                Id = user.Id,
                Username = user.Username,
                Email = user.Email,
                IsAdmin = user.IsAdmin
            }
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private async Task ClaimModAuthorshipByNameAsync(AppUser appUser)
    {
        var modsToClaim = db.ModGroups.Where(w => w.Author == appUser.Username).ToArray();
        foreach (var mod in modsToClaim)
            mod.OwnerId = appUser.Id;

        await db.SaveChangesAsync();
        logger.LogInformation("User '{username}' claimed mod authorship for: {mods}", appUser.Username, string.Join(", ", modsToClaim.Select(m => m.Id)));
    }

    private async Task<string> ResolveUniqueUsernameAsync(
        string f95Username, string f95UserId, CancellationToken ct)
    {
        // Try the bare F95 username first; fall back to username_userId if taken.
        if (!await db.Users.AnyAsync(u => u.Username == f95Username, ct))
            return f95Username;

        var fallback = $"{f95Username}_{f95UserId}";
        if (!await db.Users.AnyAsync(u => u.Username == fallback, ct))
            return fallback;

        // Last resort: append a random suffix.
        return $"{f95Username}_{Guid.NewGuid():N}"[..Math.Min(50, f95Username.Length + 33)];
    }

    private string GenerateToken(AppUser user)
    {
        var secret = config["Jwt:Secret"]
            ?? throw new InvalidOperationException("JWT secret is not configured.");

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim("sub",   user.Id.ToString()),
            new Claim("name",  user.Username),
            new Claim("email", user.Email),
            new Claim("role",  user.IsAdmin ? "Admin" : "User"),
        };

        var token = new JwtSecurityToken(
            issuer: Configuration.AppName,
            audience: Configuration.AppName,
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
