using F3M.Server.Controllers;
using F3M.Server.Models;
using F3M.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace F3M.Server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ModGroup> ModGroups => Set<ModGroup>();
    public DbSet<Mod> Mods => Set<Mod>();
    public DbSet<ModFile> ModFiles => Set<ModFile>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<F95PendingVerification> F95PendingVerifications => Set<F95PendingVerification>();
    public DbSet<Telemetry.ErrorReport> TelemetryErrorReports => Set<Telemetry.ErrorReport>();

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
            e.HasMany(m => m.Files)
             .WithOne()
             .HasForeignKey(f => f.ModId)
             .OnDelete(DeleteBehavior.Cascade);
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
            // Email is empty string for F95-linked accounts; unique constraint removed.
            e.Property(u => u.Email).HasMaxLength(200);
            e.Property(u => u.IsAdmin).HasDefaultValue(false);
            e.Property(u => u.F95UserId).HasMaxLength(30);
            e.Property(u => u.F95Username).HasMaxLength(50);
            e.HasIndex(u => u.Username).IsUnique();
            // Partial unique index: only enforce uniqueness when F95UserId is set.
            e.HasIndex(u => u.F95UserId).IsUnique().HasFilter("\"F95UserId\" IS NOT NULL");
        });

        modelBuilder.Entity<F95PendingVerification>(e =>
        {
            e.HasKey(v => v.Id);
            e.Property(v => v.F95UserId).IsRequired().HasMaxLength(30);
            e.Property(v => v.F95Username).IsRequired().HasMaxLength(50);
            e.Property(v => v.VerificationGuid).IsRequired().HasMaxLength(40);
            e.Property(v => v.Status).HasConversion<string>();
            e.HasIndex(v => v.F95UserId);
            e.HasIndex(v => v.Status);
        });

#if DEBUG
        var name = nameof(F3M);
        modelBuilder.Entity<AppUser>().HasData(
            new AppUser
            {
                Id           = 1,
                Username     = name,
                PasswordHash = AuthController.HashPassword(name),
                IsAdmin      = true,
                Email        = $"{name}-admin@example.com",
                RegisteredAt = DateTime.UtcNow
            }
        );
#endif
    }
}
