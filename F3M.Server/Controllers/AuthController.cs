using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using F3M.Server.Data;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace F3M.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(AppDbContext db, IConfiguration config, ILogger<AuthController> logger) : ControllerBase
{
    // POST /api/auth/register
    [HttpPost("register")]
    public async Task<ActionResult<AuthResult>> Register([FromBody] RegisterDto dto)
    {
        if (!ModelState.IsValid)
            return BadRequest(new AuthResult { Success = false, Error = "Invalid input." });

        if (await db.Users.AnyAsync(u => u.Username == dto.Username))
            return Conflict(new AuthResult { Success = false, Error = "Username is already taken." });

        if (await db.Users.AnyAsync(u => u.Email == dto.Email))
            return Conflict(new AuthResult { Success = false, Error = "Email is already registered." });

        var user = new AppUser
        {
            Username     = dto.Username,
            Email        = dto.Email,
            PasswordHash = HashPassword(dto.Password),
            RegisteredAt = DateTime.UtcNow
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        logger.LogInformation("New user registered: {Username}", user.Username);

        var token = GenerateToken(user);
        return Ok(new AuthResult
        {
            Success = true,
            Token   = token,
            User    = new UserInfo { Id = user.Id, Username = user.Username, Email = user.Email, IsAdmin = user.IsAdmin }
        });
    }

    // POST /api/auth/login
    [HttpPost("login")]
    public async Task<ActionResult<AuthResult>> Login([FromBody] LoginDto dto)
    {
        var user = await db.Users.FirstOrDefaultAsync(u =>
            u.Username == dto.UsernameOrEmail || u.Email == dto.UsernameOrEmail);

        if (user is null || !VerifyPassword(dto.Password, user.PasswordHash))
            return Unauthorized(new AuthResult { Success = false, Error = "Invalid credentials." });

        var token = GenerateToken(user);
        return Ok(new AuthResult
        {
            Success = true,
            Token   = token,
            User    = new UserInfo { Id = user.Id, Username = user.Username, Email = user.Email, IsAdmin = user.IsAdmin }
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string GenerateToken(AppUser user)
    {
        var secret = config["Jwt:Secret"] ?? "f3m-super-secret-key-change-in-production-32chars!";
        var key    = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds  = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        // Use short JWT claim names to avoid the long-URI remapping mismatch
        // between JwtSecurityTokenHandler write and read paths.
        var claims = new[]
        {
            new Claim("sub",   user.Id.ToString()),
            new Claim("name",  user.Username),
            new Claim("email", user.Email),
            new Claim("role",  user.IsAdmin ? "Admin" : "User"),
        };

        var token = new JwtSecurityToken(
            issuer:   "f3m",
            audience: "f3m",
            claims:   claims,
            expires:  DateTime.UtcNow.AddDays(30),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string HashPassword(string password)
    {
        var salt  = RandomNumberGenerator.GetBytes(16);
        var hash  = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt,
            iterations: 100_000, HashAlgorithmName.SHA256, outputLength: 32);
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split(':');
        if (parts.Length != 2) return false;
        var salt     = Convert.FromBase64String(parts[0]);
        var expected = Convert.FromBase64String(parts[1]);
        var actual   = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt,
            iterations: 100_000, HashAlgorithmName.SHA256, outputLength: 32);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
