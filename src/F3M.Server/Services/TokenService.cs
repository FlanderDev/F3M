using F3M.Server.Models;
using F3M.Shared;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace F3M.Server.Services;

/// <summary>
/// Issues JWTs and builds the client-facing UserInfo DTO, pulling roles from
/// Identity's UserManager (AspNetUserRoles) rather than a bool on the user entity.
/// </summary>
public class TokenService(UserManager<AppUser> userManager, IConfiguration config)
{
    public async Task<string> GenerateTokenAsync(AppUser user)
    {
        var secret = config["Jwt:Secret"] ?? throw new InvalidOperationException("JWT secret is not configured.");
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var roles = await userManager.GetRolesAsync(user);

        // Use short JWT claim names to avoid the long-URI remapping mismatch
        // between JwtSecurityTokenHandler write and read paths.
        var claims = new List<Claim>
        {
            new("sub",   user.Id.ToString()),
            new("name",  user.UserName ?? string.Empty),
            new("email", user.Email ?? string.Empty),
        };

        // One claim per role — ClaimsIdentity/IsInRole and [Authorize(Roles=...)] both
        // handle multiple claims of the same RoleClaimType ("role") correctly.
        claims.AddRange(roles.Select(r => new Claim("role", r)));

        var token = new JwtSecurityToken(
            issuer: Configuration.AppName,
            audience: Configuration.AppName,
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task<UserInfo> BuildUserInfoAsync(AppUser user)
    {
        var roles = await userManager.GetRolesAsync(user);
        return new UserInfo
        {
            Id = user.Id,
            Username = user.UserName ?? string.Empty,
            Email = user.Email ?? string.Empty,
            IsAdmin = roles.Contains(AppRoles.Admin),
            Roles = [.. roles]
        };
    }
}
