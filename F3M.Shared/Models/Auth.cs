using System.ComponentModel.DataAnnotations;

namespace F3M.Shared.Models;

public class AppUser
{
    public int Id { get; set; }

    [Required, MaxLength(50)]
    public string Username { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string Email { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;

    public bool IsAdmin { get; set; } = false;
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
    public bool      Success { get; set; }
    public string?   Error   { get; set; }
    public string?   Token   { get; set; }
    public UserInfo? User    { get; set; }
}

public class UserInfo
{
    public int    Id       { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email    { get; set; } = string.Empty;
    public bool   IsAdmin  { get; set; }
}

/// <summary>Returned by admin user-list endpoint.</summary>
public class AdminUserDto
{
    public int      Id           { get; set; }
    public string   Username     { get; set; } = string.Empty;
    public string   Email        { get; set; } = string.Empty;
    public bool     IsAdmin      { get; set; }
    public DateTime RegisteredAt { get; set; }
    public int      ModCount     { get; set; }
}

/// <summary>Mod edit payload — metadata only; files are managed separately via upload.</summary>
public class ModEditDto
{
    [Required, MaxLength(120)]
    public string Name        { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string Description { get; set; } = string.Empty;

    [MaxLength(20)]
    public string Version     { get; set; } = "1.0.0";

    [MaxLength(50)]
    public string Category    { get; set; } = "General";
}
