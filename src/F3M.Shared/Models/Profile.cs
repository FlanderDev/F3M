using System.ComponentModel.DataAnnotations;

namespace F3M.Shared.Models;

/// <summary>The current user's own account info — self-service view, not the admin user list.</summary>
public class ProfileDto
{
    public string Username { get; set; } = string.Empty;

    /// <summary>Empty for F95-linked accounts, which don't collect one.</summary>
    public string Email { get; set; } = string.Empty;
    public bool IsEmailConfirmed { get; set; }

    /// <summary>Null if this account was never linked to F95zone.</summary>
    public string? F95Username { get; set; }
    public string? F95UserId { get; set; }

    public DateTime RegisteredAt { get; set; }
    public List<string> Roles { get; set; } = [];
    public int ModCount { get; set; }
}

public class ChangePasswordDto
{
    [Required]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required]
    [MinLength(Configuration.PasswordMinLength, ErrorMessage = "The password must be at least {1} characters long.")]
    [MaxLength(Configuration.PasswordMaxLength, ErrorMessage = "The password can be at most {1} characters long.")]
    public string NewPassword { get; set; } = string.Empty;

    [Required, Compare(nameof(NewPassword), ErrorMessage = "Passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

/// <summary>Public-facing view of another user's account — deliberately excludes
/// email, roles, and F95 link details, unlike ProfileDto (the self-view).</summary>
public class PublicProfileDto
{
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// False for an author without an F3M account, such as the uploader of mods imported from F95zone: the page then
    /// lists the mods published under that name, and <see cref="RegisteredAt"/> is not set.
    /// </summary>
    public bool HasAccount { get; set; } = true;

    public DateTime RegisteredAt { get; set; }
    public int ModCount { get; set; }
    public List<Mod> Mods { get; set; } = [];
}
