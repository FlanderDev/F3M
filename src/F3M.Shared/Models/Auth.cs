using System.ComponentModel.DataAnnotations;

namespace F3M.Shared.Models;

public class AppUser
{
    public int Id { get; set; }

    [Required, MaxLength(50)]
    public string Username { get; set; } = string.Empty;

    // Empty string for F95-linked accounts (no email registration).
    [MaxLength(200)]
    public string Email { get; set; } = string.Empty;

    // Empty string for F95-linked accounts (no password).
    public string PasswordHash { get; set; } = string.Empty;

    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;

    public bool IsAdmin { get; set; } = false;

    // F95zone account link — null for legacy password accounts.
    public string? F95UserId { get; set; }
    public string? F95Username { get; set; }
}

public class RegisterDto
{
    [Required, MaxLength(50), MinLength(3)]
    public string Username { get; set; } = string.Empty;

    [Required, EmailAddress, MaxLength(200)]
    public string Email { get; set; } = string.Empty;

    [Required, MinLength(8), MaxLength(100)]
    public string Password { get; set; } = string.Empty;

    [Required]
    [Compare(nameof(Password), ErrorMessage = "Passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public class LoginDto
{
    [Required]
    public string UsernameOrEmail { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}

public class AuthResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Token { get; set; }
    public UserInfo? User { get; set; }
}

public class UserInfo
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
}

/// <summary>Returned by admin user-list endpoint.</summary>
public class AdminUserDto
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public DateTime RegisteredAt { get; set; }
    public int ModCount { get; set; }
}

/// <summary>Mod edit payload — metadata only; files are managed separately via upload.</summary>
public class ModEditDto
{
    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Description { get; set; } = string.Empty;

    [MaxLength(20)]
    public string Version { get; set; } = "1.0.0";

    [MaxLength(50)]
    public string Category { get; set; } = "General";
}

// ── F95zone account linking DTOs ──────────────────────────────────────────────

/// <summary>Sent by the client to kick off a verification flow.</summary>
public class LinkF95StartRequest
{
    [Required]
    public string ProfileUrl { get; set; } = string.Empty;
}

/// <summary>Returned after a verification has been started — tells the client what code to post and where.</summary>
public class LinkF95StartResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? VerificationGuid { get; set; }
    public string? F95UserId { get; set; }
    public string? F95Username { get; set; }
}

/// <summary>Sent by the client when polling for a GUID reply.</summary>
public class LinkF95PollRequest
{
    [Required, MinLength(8), MaxLength(100)]
    public string Password { get; set; } = string.Empty;
}

/// <summary>Returned each time the client polls for a GUID reply.</summary>
public class LinkF95PollResponse
{
    /// <summary>One of: Pending, Verified, Expired, NotFound.</summary>
    public VerificationState Status { get; set; } = VerificationState.None;
    public string? Message { get; set; }

    // Populated only when Status == "Verified".
    public string? Token { get; set; }
    public UserInfo? User { get; set; }
}
