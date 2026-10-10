using System.ComponentModel.DataAnnotations;

namespace F3M.Shared.Models;

public class LoginDto
{
    [Required]
    public string UsernameOrEmail { get; set; } = string.Empty;

    [Required]
    public string Password { get; set; } = string.Empty;
}

/// <summary>Returned by admin user-list endpoint.</summary>
public class AdminUserDto
{
    public int Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsAdmin { get; set; }
    public List<string> Roles { get; set; } = [];
    public DateTime RegisteredAt { get; set; }
    public int ModCount { get; set; }
}

/// <summary>Mod edit payload — metadata only; files are managed separately via upload.</summary>
public class ModEditDto
{
    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [Required, MaxLength(Configuration.ModDescriptionMaxSize)]
    public string Description { get; set; } = string.Empty;

    [MaxLength(20)]
    public string Version { get; set; } = "1.0.0";

    [MaxLength(50)]
    public string Category { get; set; } = "General";

    /// <summary>ModGroup IDs (logical mods) this version depends on. Replaces the full set —
    /// send everything that should remain, not just additions/removals.</summary>
    public List<int> DependencyGroupIds { get; set; } = [];
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

    /// <summary>
    /// Private proof that this browser started the verification. Never posted anywhere; sent back with every check.
    /// </summary>
    public string? ClientToken { get; set; }

    /// <summary>The F3M username: the existing account's name, or the name a new account will get.</summary>
    public string? Username { get; set; }

    /// <summary>True when an F3M account is already linked to this F95 user. Verifying signs in and sets a new password.</summary>
    public bool ExistingAccount { get; set; }

    /// <summary>
    /// True when the start was refused because another F3M account already has the F95 name. An admin has to resolve it.
    /// </summary>
    public bool UsernameTaken { get; set; }
}

/// <summary>Sent by the client when polling for a GUID reply.</summary>
public class LinkF95PollRequest
{
    [Required, MinLength(8), MaxLength(100)]
    public string Password { get; set; } = string.Empty;

    /// <summary>The <see cref="LinkF95StartResponse.ClientToken"/> from the start of this verification.</summary>
    [Required, MaxLength(100)]
    public string ClientToken { get; set; } = string.Empty;
}

/// <summary>Returned each time the client polls for a GUID reply.</summary>
public class LinkF95PollResponse
{
    /// <summary>One of: Pending, Verified, Expired, NotFound.</summary>
    public VerificationState Status { get; set; } = VerificationState.None;
    public string? Message { get; set; }

    /// <summary>On success: the username to sign in with from now on.</summary>
    public string? Username { get; set; }

    /// <summary>On success: true when a new account was created, false when an existing one was signed in.</summary>
    public bool IsNewUser { get; set; }
}
