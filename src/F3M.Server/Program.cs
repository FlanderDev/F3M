using F3M.Server.Data;
using F3M.Server.Models;
using F3M.Server.Services;
using F3M.Shared;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

#if !DEBUG
try
{
#endif

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

#region FileSystemPreparation
Directory.CreateDirectory(Assets.Files);
Directory.CreateDirectory(Assets.Images);

var databaseDirectory = Path.Combine(Assets.StorageRoot, "Database");
Directory.CreateDirectory(databaseDirectory);
#endregion

var connectionString = $"Data Source={Path.Combine(databaseDirectory, $"{Configuration.AppName}.db")}";
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

// AddIdentityCore (not AddIdentity) — AddIdentity also wires up a default UI/external
// login providers we don't use. Cookie auth itself is added explicitly below via
// AddAuthentication/AddIdentityCookies. Password/lockout policy stays close to what
// the old hand-rolled hasher enforced.
builder.Services
    .AddIdentityCore<AppUser>(options =>
    {
        options.Password.RequiredLength = 8;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
        options.User.RequireUniqueEmail = false; // F95-linked accounts have no email.
    })
    .AddRoles<IdentityRole<int>>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders()
    .AddSignInManager();

// F95Service holds the XenForo login session (cookie jar) — must be singleton.
builder.Services.AddSingleton<F95Service>();

builder.Services
        .AddAuthentication(IdentityConstants.ApplicationScheme)
        .AddIdentityCookies();

// This is an API, not a page app — without this, an unauthenticated request to any
// [Authorize]-protected endpoint gets a 302 redirect to a nonexistent "/Account/Login"
// page instead of a clean 401 (and 403 for role/policy failures). The client's fetch
// follows that redirect silently, so instead of a clean 401 it either 404s or — if a
// SPA fallback route is registered — gets back 200 + the index.html shell, which then
// fails to parse as the expected JSON. Both cases are swallowed by a generic catch in
// CookieAuthenticationStateProvider so the app doesn't crash, but every anonymous
// visit was logging a spurious error and wasting a redirect round-trip.
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Events.OnRedirectToLogin = context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = context =>
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    };
});

builder.Services.AddAuthorizationBuilder();

var app = builder.Build();

// Hand-rolled instead of MapIdentityApi<AppUser>() — that built-in endpoint set only
// supports logging in by email (FindByEmailAsync internally), but F95-linked accounts
// are deliberately email-less and log in by username. /manage/info is reimplemented
// for the same reason: the built-in version only ever returns Email, and the client
// needs a UserName to show/derive an identity for email-less accounts.

app.MapPost("/login", async (
    LoginDto login,
    UserManager<AppUser> userManager,
    SignInManager<AppUser> signInManager) =>
{
    var user = await userManager.FindByNameAsync(login.UsernameOrEmail)
               ?? await userManager.FindByEmailAsync(login.UsernameOrEmail);

    if (user is null)
        return Results.Unauthorized();

    var result = await signInManager.CheckPasswordSignInAsync(user, login.Password, lockoutOnFailure: true);
    if (!result.Succeeded)
        return Results.Unauthorized();

    await signInManager.SignInAsync(user, isPersistent: true);
    return Results.Ok();
});

app.MapPost("/logout", async (SignInManager<AppUser> signInManager) =>
{
    await signInManager.SignOutAsync();
    return Results.Ok();
}).RequireAuthorization();

app.MapGet("/manage/info", async (ClaimsPrincipal principal, UserManager<AppUser> userManager) =>
{
    var user = await userManager.GetUserAsync(principal);
    if (user is null)
        return Results.Unauthorized();

    return Results.Ok(new
    {
        UserName = user.UserName ?? string.Empty,
        Email = user.Email ?? string.Empty,
        IsEmailConfirmed = user.EmailConfirmed
    });
}).RequireAuthorization();

// CookieAuthenticationStateProvider (client) calls this to build the role claims on its
// local ClaimsPrincipal. Claim types are passed through as-is; UserClaimsPrincipalFactory
// already issues them as ClaimTypes.Role, which is what the client mirrors them as too.
app.MapGet("/roles", (ClaimsPrincipal user) =>
{
    var roles = user.Claims
        .Where(c => c.Type == ClaimTypes.Role)
        .Select(c => new { c.Type, c.Value, c.ValueType, c.Issuer, c.OriginalIssuer });
    return Results.Ok(roles);
}).RequireAuthorization();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

#if DEBUG // Seed a debug admin user for local development/testing
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
    var name = nameof(F3M);
    var existingDebugUser = await userManager.FindByNameAsync(name);
    if (existingDebugUser is null)
    {
        var newDebugUser = new AppUser
        {
            UserName = name,
            Email = $"{name}-admin@example.com",
            RegisteredAt = DateTime.UtcNow,
        };
        newDebugUser.PasswordHash = userManager.PasswordHasher.HashPassword(newDebugUser, name);

        var createResult = await userManager.CreateAsync(newDebugUser);
        if (createResult.Succeeded)
            await userManager.AddToRoleAsync(newDebugUser, AppRoles.Admin);
        else
            throw new Exception($"Failed to create debug admin user: {string.Join(", ", createResult.Errors.Select(e => e.Description))}");
    }
#endif
}

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// IMPORTANT: UseBlazorFrameworkFiles must come before UseStaticFiles and UseRouting.
// It registers the /_framework/* routes that serve the WASM boot files with the
// correct application/wasm and text/javascript MIME types. Without this ordering,
// those requests fall through to MapFallbackToFile and return text/html, which
// browsers refuse to execute as modules.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

// Serve user-uploaded mod files and preview thumbnails from the persistent asset directory (outside wwwroot)
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.Combine(Environment.CurrentDirectory, Assets.PublicContent)), // FileSystem Path
    RequestPath = Assets.ServedPath // Served Path
});

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();
return 0;

#if !DEBUG
}
catch (Exception ex)
{
    Console.WriteLine($"An fatal error occurred, program '{Configuration.AppName}' shutting down failed:{Environment.NewLine}{ex.Message}");
    return 1;
}
#endif