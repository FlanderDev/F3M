using F3M.Server.Controllers;
using F3M.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace F3M.Server.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ModGroup> ModGroups => Set<ModGroup>();
    public DbSet<Mod> Mods => Set<Mod>();
    public DbSet<ModFile> ModFiles => Set<ModFile>();
    public DbSet<AppUser> Users => Set<AppUser>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<ModGroup>(e =>
        {
            e.HasKey(g => g.Id);
            e.Property(g => g.Author).IsRequired().HasMaxLength(80);
        });

        modelBuilder.Entity<Mod>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.Name).IsRequired().HasMaxLength(120);
            e.Property(m => m.Author).IsRequired().HasMaxLength(80);
            e.Property(m => m.Version).HasMaxLength(20);
            e.Property(m => m.Category).HasMaxLength(50);
            e.HasIndex(m => m.Name);
            e.HasIndex(m => m.Category);
            e.HasIndex(m => m.ModGroupId);
            e.HasIndex(m => m.IsLatestVersion);
            // Navigation: a Mod has many ModFiles
            e.HasMany(m => m.Files)
             .WithOne()
             .HasForeignKey(f => f.ModId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ModFile>(e =>
        {
            e.HasKey(f => f.Id);
            e.Property(f => f.FileName).IsRequired();
            e.Property(f => f.InstallPath).HasMaxLength(260);
        });

        modelBuilder.Entity<AppUser>(e =>
        {
            e.HasKey(u => u.Id);
            e.Property(u => u.Username).IsRequired().HasMaxLength(50);
            e.Property(u => u.Email).IsRequired().HasMaxLength(200);
            e.Property(u => u.IsAdmin).HasDefaultValue(false);
            e.HasIndex(u => u.Username).IsUnique();
            e.HasIndex(u => u.Email).IsUnique();
        });

        // ── Seed data ────────────────────────────────────────────────────────
        // Groups
        //modelBuilder.Entity<ModGroup>().HasData(
        //    new ModGroup { Id = 1, Author = "pixelcraft", OwnerId = null },
        //    new ModGroup { Id = 2, Author = "soundforge", OwnerId = null },
        //    new ModGroup { Id = 3, Author = "weathermod", OwnerId = null },
        //    new ModGroup { Id = 4, Author = "itemsmith", OwnerId = null },
        //    new ModGroup { Id = 5, Author = "soundforge", OwnerId = null },
        //    new ModGroup { Id = 6, Author = "uimod", OwnerId = null }
        //);

        //// Versions (one per group in seed)
        //modelBuilder.Entity<Mod>().HasData(
        //    new Mod { Id = 1, ModGroupId = 1, Name = "HD Texture Overhaul", Description = "Complete high-resolution texture replacement for all environments. Supports 2K and 4K output.", Author = "pixelcraft", Version = "1.0.0", Category = "Textures", DownloadCount = 3891, UploadedAt = new DateTime(2025, 2, 5, 0, 0, 0, DateTimeKind.Utc), IsApproved = true },
        //    new Mod { Id = 2, ModGroupId = 2, Name = "Ambient Sound Pack", Description = "Adds rich ambient soundscapes to all outdoor zones. Includes dynamic weather audio layers.", Author = "soundforge", Version = "2.1.0", Category = "Audio", DownloadCount = 1420, UploadedAt = new DateTime(2025, 1, 10, 0, 0, 0, DateTimeKind.Utc), IsApproved = true },
        //    new Mod { Id = 3, ModGroupId = 3, Name = "Dynamic Weather System", Description = "Realistic weather system with rain, fog, storms and seasonal cycles. Fully configurable.", Author = "weathermod", Version = "0.9.5", Category = "Gameplay", DownloadCount = 762, UploadedAt = new DateTime(2025, 3, 1, 0, 0, 0, DateTimeKind.Utc), IsApproved = true },
        //    new Mod { Id = 4, ModGroupId = 4, Name = "Extended Item Pack", Description = "Adds 200+ new craftable items, weapons and consumables with balanced progression.", Author = "itemsmith", Version = "1.1.0", Category = "Items", DownloadCount = 2105, UploadedAt = new DateTime(2025, 3, 18, 0, 0, 0, DateTimeKind.Utc), IsApproved = true },
        //    new Mod { Id = 5, ModGroupId = 5, Name = "Orchestral Soundtrack", Description = "Full OST replacement featuring an original orchestral score. 3 hours of new music.", Author = "soundforge", Version = "3.0.0", Category = "Audio", DownloadCount = 504, UploadedAt = new DateTime(2025, 4, 2, 0, 0, 0, DateTimeKind.Utc), IsApproved = true },
        //    new Mod { Id = 6, ModGroupId = 6, Name = "UI Overhaul", Description = "Clean, modern HUD redesign with better inventory management and readable fonts.", Author = "uimod", Version = "1.5.2", Category = "UI", DownloadCount = 3100, UploadedAt = new DateTime(2025, 4, 5, 0, 0, 0, DateTimeKind.Utc), IsApproved = true }
        //);

        //// Seed files (placeholder filenames — real files won't exist, but schema is valid)
        //modelBuilder.Entity<ModFile>().HasData(
        //    new ModFile { Id = 1, ModId = 1, FileName = "seed_hd_textures.zip", OriginalName = "hd_textures_v1.0.zip", InstallPath = "Manaka/Mods/Textures", FileSizeBytes = 512_000_000 },
        //    new ModFile { Id = 2, ModId = 2, FileName = "seed_ambient.zip", OriginalName = "ambient_sounds_v2.1.zip", InstallPath = "Manaka/Mods/Audio", FileSizeBytes = 48_000_000 },
        //    new ModFile { Id = 3, ModId = 3, FileName = "seed_weather.zip", OriginalName = "weather_system_v0.9.5.zip", InstallPath = "Manaka/Mods/Gameplay", FileSizeBytes = 8_200_000 },
        //    new ModFile { Id = 4, ModId = 4, FileName = "seed_items.zip", OriginalName = "item_pack_v1.1.zip", InstallPath = "Manaka/Mods/Items", FileSizeBytes = 3_400_000 },
        //    new ModFile { Id = 5, ModId = 5, FileName = "seed_ost.zip", OriginalName = "ost_orchestral_v3.0.zip", InstallPath = "Manaka/Mods/Audio", FileSizeBytes = 220_000_000 },
        //    new ModFile { Id = 6, ModId = 6, FileName = "seed_ui.zip", OriginalName = "ui_overhaul_v1.5.2.zip", InstallPath = "Manaka/Mods/UI", FileSizeBytes = 1_800_000 }
        //);

#if DEBUG
        modelBuilder.Entity<AppUser>().HasData(
            new AppUser
            {
                Id = 1,
                Username = "FlanAdmin",
                PasswordHash = AuthController.HashPassword("FlanAdminPassword"),
                IsAdmin = true,
                Email = "admin@example.com",
                RegisteredAt = DateTime.UtcNow
            }
        );
#endif
    }
}
