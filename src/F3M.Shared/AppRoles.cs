namespace F3M.Shared;

/// <summary>
/// Role name constants shared by client (AuthorizeView/[Authorize] on Blazor pages) and
/// server (Identity RoleManager/UserManager, [Authorize(Roles=...)] on controllers).
/// Kept as consts so they remain usable in attributes.
/// </summary>
public static class AppRoles
{
    public const string Admin = "Admin";
    public const string User = "User";

    public static readonly string[] All = [Admin, User];
}
