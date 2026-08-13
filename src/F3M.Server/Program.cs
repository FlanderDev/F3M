using F3M.Server.Data;
using F3M.Server.Models;
using F3M.Server.Services;
using F3M.Shared;
using F3M.Shared.Helpers;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;
using System.Text;

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

// AddIdentityCore (not AddIdentity) — this is an API, so we don't want the cookie
// auth scheme, external login providers, or Razor Pages UI that AddIdentity wires up.
// Password/lockout policy stays close to what the old hand-rolled hasher enforced.
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
    .AddDefaultTokenProviders();

// F95Service holds the XenForo login session (cookie jar) — must be singleton.
builder.Services.AddSingleton<F95Service>();
builder.Services.AddScoped<TokenService>();

string? jwtSecret = builder.Configuration["Jwt:Secret"];
if (string.IsNullOrWhiteSpace(jwtSecret))
{
    var secretPath = Path.Combine(Assets.StorageRoot, "secret.txt");
    if (File.Exists(secretPath))
    {
        jwtSecret = (await File.ReadAllLinesAsync(secretPath)).LastOrDefault() ?? throw new InvalidOperationException("Failed to read JWT secret from file.");
        return -1;
    }

    using var rng = RandomNumberGenerator.Create();
    var bytes = new byte[32]; // 256 bits
    rng.GetBytes(bytes);
    jwtSecret = Convert.ToBase64String(bytes);
    await File.WriteAllLinesAsync(secretPath, [DateTime.Now.ToString(), jwtSecret]);
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Convert.FromBase64String(jwtSecret)),

            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();
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
            Console.WriteLine($"Failed to seed debug admin: {string.Join(", ", createResult.Errors.Select(e => e.Description))}");
    }


    if (existingDebugUser is not null) // Remove this if
    {
        var result = await userManager.AddToRoleAsync(existingDebugUser, AppRoles.Admin);
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

app.MapGet("/x", () => "Hello, World!");

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