using F3M.Server.Models;
using F3M.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace F3M.Server.Services.Setup;

/// <summary>
/// The admin account from the server settings. Bound from the "Admin" section: Admin__UserName, Admin__Password and
/// Admin__OverwritePassword (F3M_ADMIN_* in .env).
/// </summary>
public sealed class AdminAccountOptions
{
    public const string SectionName = "Admin";

    public string? UserName { get; set; }

    /// <summary>Used when the account is created, and on every start when <see cref="OverwritePassword"/> is set.</summary>
    public string? Password { get; set; }

    /// <summary>Resets the password to <see cref="Password"/> on every start, e.g. after it was lost.</summary>
    public bool OverwritePassword { get; set; }
}

/// <summary>
/// Makes sure the admin account from the settings exists and is an admin, at every start: it is created when missing
/// (an existing account of that name, e.g. one linked to F95zone, simply becomes admin), and its password is only
/// changed when <see cref="AdminAccountOptions.OverwritePassword"/> says so. Problems are logged and shown on the setup
/// page; they never stop the server.
/// </summary>
public sealed class AdminAccount(IOptions<AdminAccountOptions> options, IHostEnvironment environment, ILogger<AdminAccount> logger)
{
    private readonly AdminAccountOptions settings = options.Value;

    /// <summary>The configured user name; null when none is set.</summary>
    public string? UserName => string.IsNullOrWhiteSpace(settings.UserName) ? null : settings.UserName.Trim();

    public bool OverwritePassword => settings.OverwritePassword;

    /// <summary>Why the account could not be set up at the last start; null when it worked or none is configured.</summary>
    public string? Problem { get; private set; }

    /// <summary>True for the account from the settings, which can't be demoted or deleted on the admin page.</summary>
    public bool Is(string? userName) =>
        UserName is { } name && string.Equals(name, userName, StringComparison.OrdinalIgnoreCase);

    public async Task EnsureAsync(UserManager<AppUser> users)
    {
        if (UserName is not { } name)
            return;

        try
        {
            Problem = await EnsureCoreAsync(users, name);
        }
        catch (Exception ex)
        {
            Problem = $"Unexpected error: {ex.Message}";
        }

        if (Problem is not null)
            logger.LogError("The admin account {UserName} from the settings could not be set up: {Problem}", name, Problem);
    }

    private async Task<string?> EnsureCoreAsync(UserManager<AppUser> users, string name)
    {
        var password = settings.Password ?? string.Empty;
        var user = await users.FindByNameAsync(name);

        if (user is null)
        {
            if (password.Length == 0)
                return "F3M_ADMIN_PASSWORD is not set, so the account can't be created.";

            user = new AppUser { UserName = name, RegisteredAt = DateTime.UtcNow };
            if (await CheckPasswordAsync(users, user, password) is { } weak)
                return weak;

            user.PasswordHash = users.PasswordHasher.HashPassword(user, password);
            var created = await users.CreateAsync(user);
            if (!created.Succeeded)
                return Describe(created);

            logger.LogInformation("Created the admin account {UserName} from the settings.", name);
        }
        else if (settings.OverwritePassword)
        {
            if (password.Length == 0)
                return "F3M_ADMIN_PASSWORD_OVERWRITE is on, but F3M_ADMIN_PASSWORD is not set.";

            var same = user.PasswordHash is not null
                       && users.PasswordHasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;
            if (!same)
            {
                if (await CheckPasswordAsync(users, user, password) is { } weak)
                    return weak;

                // Set directly, like on creation: the password rules are checked above, and only outside Development.
                user.PasswordHash = users.PasswordHasher.HashPassword(user, password);
                var updated = await users.UpdateAsync(user);
                if (!updated.Succeeded)
                    return Describe(updated);

                await users.UpdateSecurityStampAsync(user); // signs out the sessions that used the old password
                logger.LogWarning("The password of the admin account {UserName} was reset from the settings.", name);
            }

            await users.SetLockoutEndDateAsync(user, null);
            await users.ResetAccessFailedCountAsync(user);
        }

        foreach (var role in new[] { AppRoles.User, AppRoles.Admin })
        {
            if (await users.IsInRoleAsync(user, role))
                continue;

            var added = await users.AddToRoleAsync(user, role);
            if (!added.Succeeded)
                return Describe(added);
        }

        return null;
    }

    /// <summary>The site's password rules. Development skips them, so the local admin can keep a short password.</summary>
    private async Task<string?> CheckPasswordAsync(UserManager<AppUser> users, AppUser user, string password)
    {
        if (environment.IsDevelopment())
            return null;

        foreach (var validator in users.PasswordValidators)
        {
            var result = await validator.ValidateAsync(users, user, password);
            if (!result.Succeeded)
                return "F3M_ADMIN_PASSWORD: " + Describe(result);
        }

        return null;
    }

    private static string Describe(IdentityResult result) => string.Join(" ", result.Errors.Select(e => e.Description));
}
