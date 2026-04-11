using System.Text;
using F3M.Server.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=Database/f3m.db";
var dir = Directory.GetParent(connectionString.Replace("Data Source=", string.Empty));
if (dir != null)
    Directory.CreateDirectory(dir.FullName);
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

var jwtSecret = builder.Configuration["Jwt:Secret"] ?? "f3m-super-secret-key-change-in-production-32chars!";
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

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();
