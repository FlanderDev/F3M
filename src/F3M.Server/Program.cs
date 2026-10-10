using F3M.Server.Data;
using F3M.Server.Models;
using F3M.Server.Services;
using F3M.Shared;
using F3M.Shared.Api;
using F3M.Shared.Helpers;
using F3M.Shared.Models;
using Microsoft.AspNetCore.DataProtection;
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

builder.Services.Configure<CatalogOptions>(builder.Configuration.GetSection(CatalogOptions.SectionName));
builder.Services.AddScoped<CatalogService>();

#region FileSystemPreparation
Directory.CreateDirectory(Assets.Files);
Directory.CreateDirectory(Assets.Images);

var catalogOptions = builder.Configuration.GetSection(CatalogOptions.SectionName).Get<CatalogOptions>() ?? new CatalogOptions();
Directory.CreateDirectory(catalogOptions.Directory);

var databaseDirectory = Path.Combine(Assets.StorageRoot, "Database");
Directory.CreateDirectory(databaseDirectory);
#endregion

// The keys that protect login cookies live in the storage volume, so sign-ins survive container rebuilds.
// Without this they sit inside the container and every redeploy signs everyone out.
builder.Services.AddDataProtection()
    .SetApplicationName(Configuration.AppName)
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(Assets.StorageRoot, "Secrets", "DataProtection")));

var connectionString = $"Data Source={Path.Combine(databaseDirectory, $"{Configuration.AppName}.db")}";
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

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
    .AddSignInManager()
    .AddDefaultTokenProviders();

// F95Service holds the XenForo login session (cookie jar) — must be singleton.
builder.Services.AddSingleton<F95Service>();

// ProfileService needs the current HttpContext to resolve "the current user".
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IProfileApi, ProfileService>();
builder.Services.AddScoped<IAdminApi, AdminService>();
builder.Services.AddScoped<IF95LinkApi, F95LinkService>();
builder.Services.AddScoped<IModsApi, ModsService>();
builder.Services.AddScoped<ModUploadService>();
builder.Services.AddScoped<ModOwnershipService>();

builder.Services
        .AddAuthentication(IdentityConstants.ApplicationScheme)
        .AddIdentityCookies();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Events.OnRedirectToLogin = async context => context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    options.Events.OnRedirectToAccessDenied = async context => context.Response.StatusCode = StatusCodes.Status403Forbidden;
});

builder.Services.AddAuthorizationBuilder();
builder.Services.AddValidation();

var app = builder.Build();

#region Endpoints
#region Common Redirects
app.MapGet("/login", () => Results.Redirect(Paths.Login));
#endregion

// MapIdentityApi<AppUser>() built-in endpoint set only supports logging in by email,
// but F95-linked accounts are email-less by design and usess username.
app.MapPost("/login", async (
    LoginDto login,
    UserManager<AppUser> userManager,
    SignInManager<AppUser> signInManager,
    ModOwnershipService ownership) =>
{
    var user = await userManager.FindByNameAsync(login.UsernameOrEmail)
               ?? await userManager.FindByEmailAsync(login.UsernameOrEmail);

    if (user is null)
        return Results.Unauthorized();

    var result = await signInManager.CheckPasswordSignInAsync(user, login.Password, lockoutOnFailure: true);
    if (result.IsLockedOut)
        return Results.StatusCode(StatusCodes.Status429TooManyRequests); // The login page explains the wait.
    if (!result.Succeeded)
        return Results.Unauthorized();

    await signInManager.SignInAsync(user, isPersistent: true);

    // Mods imported for this user's F95 account since the last sign-in become theirs.
    await ownership.ClaimAllAsync(user);
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
#endregion

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
    await scope.ServiceProvider.GetRequiredService<CatalogService>().InitializeAsync();

    // Seed a debug admin user for local development/testing
#if DEBUG
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
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

// Serve preview thumbnails from the persistent asset directory (outside wwwroot).
// Mod files are deliberately NOT served from here: they are only reachable through the download
// endpoint, which counts the download. (They used to sit under the same public folder, so anyone who
// had seen a file's generated name in the API could fetch it directly.)
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.Combine(Environment.CurrentDirectory, Assets.Images)), // FileSystem Path
    RequestPath = $"{Assets.ServedPath}/{nameof(Assets.Images)}", // Served Path, same as Assets.ImageUrl()
    OnPrepareResponse = context => context.Context.Response.Headers.XContentTypeOptions = "nosniff"
});

// The public catalog: signed JSON read by the desktop app. Read-only, with a short cache so the index stays fresh.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(catalogOptions.Directory)),
    RequestPath = "/catalog",
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = "public, max-age=60";
        context.Context.Response.Headers.XContentTypeOptions = "nosniff";
    }
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