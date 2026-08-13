using Microsoft.AspNetCore.Identity;

namespace F3M.Server.Models;

/// <summary>
/// The Identity user entity. Lives server-side only — the client never sees this type,
/// only the plain DTOs in F3M.Shared.Models (AdminUserDto, etc.).
/// Roles are managed separately via RoleManager/UserManager and stored in the standard
/// Identity tables (AspNetRoles, AspNetUserRoles) rather than as a bool on this class.
/// </summary>
public class AppUser : IdentityUser<int>
{
    public DateTime RegisteredAt { get; set; } = DateTime.UtcNow;

    // F95zone account link — null for legacy password-only accounts.
    public string? F95UserId { get; set; }
    public string? F95Username { get; set; }
}
