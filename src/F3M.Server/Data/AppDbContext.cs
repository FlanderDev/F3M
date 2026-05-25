using F3M.Server.Controllers;
using F3M.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace F3M.Server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
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
            // Self-referencing many-to-many: a Mod can depend on many other Mods
            e.HasMany(m => m.Dependencies)
             .WithMany()
             .UsingEntity<Dictionary<string, object>>(
                 "ModDependency",
                 r => r.HasOne<Mod>().WithMany().HasForeignKey("DependencyId").OnDelete(DeleteBehavior.Restrict),
                 l => l.HasOne<Mod>().WithMany().HasForeignKey("ModId").OnDelete(DeleteBehavior.Cascade),
                 j =>
                 {
                     j.HasKey("ModId", "DependencyId");
                     j.ToTable("ModDependencies");
                 });
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


#if DEBUG
        var name = nameof(F3M);
        modelBuilder.Entity<AppUser>().HasData(
            new AppUser
            {
                Id = 1,
                Username = name,
                PasswordHash = AuthController.HashPassword(name),
                IsAdmin = true,
                Email = $"{name}-admin@example.com",
                RegisteredAt = DateTime.UtcNow
            }
        );
#endif
    }
}
