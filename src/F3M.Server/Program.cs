using System.Text;
using F3M.Server.Data;
using F3M.Shared.Helpers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Services.AddControllersWithViews();
    builder.Services.AddRazorPages();

    #region FileSystemPreparation
    A.AssetDir = builder.Configuration["AssetPath"] ?? string.Empty;
    Directory.CreateDirectory(A.FileDir);
    Directory.CreateDirectory(A.ImageDir);

    var databaseDirectory = Path.Combine(A.AssetDir, "Database");
    Directory.CreateDirectory(databaseDirectory);
    #endregion

    var connectionString = $"Data Source={Path.Combine(databaseDirectory, "f3m.db")}";
    builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

    var jwtSecret = builder.Configuration["Jwt:Secret"];
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = "f3m",
                ValidAudience = "f3m",
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                // Keep claim names as written in the JWT ("role", "sub", etc.)
                // instead of remapping to long CLR URIs. Matches client-side parsing.
                RoleClaimType = "role",
                NameClaimType = "name"
            };
        });

    builder.Services.AddAuthorization();

    var app = builder.Build();
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.EnsureCreated();
        db.Database.Migrate();
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

    // Serve user-uploaded mod files and preview thumbnails from the persistent
    // asset directory (outside wwwroot) at the /assets URL prefix.
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.Combine(Environment.CurrentDirectory, A.AssetDir)), // FileSystem Path
        RequestPath = "/assets" // Served Path
    });

    app.UseRouting();

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapRazorPages();
    app.MapControllers();
    app.MapFallbackToFile("index.html");

    app.Run();
}
catch (Exception ex)
{

}