using F3M.Server.Models;
using F3M.Shared;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace F3M.Server.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<int>, int>(options)
{
    //public DbSet<ModGroup> AspNetUsers => Set<ModGroup>();
    public DbSet<ModGroup> ModGroups => Set<ModGroup>();
    public DbSet<Mod> Mods => Set<Mod>();
    public DbSet<ModFile> ModFiles => Set<ModFile>();
    public DbSet<ModFileEntry> ModFileEntries => Set<ModFileEntry>();
    public DbSet<ModGeneratedPath> ModGeneratedPaths => Set<ModGeneratedPath>();
    public DbSet<F95PendingVerification> F95PendingVerifications => Set<F95PendingVerification>();
    public DbSet<Telemetry.ErrorReport> TelemetryErrorReports => Set<Telemetry.ErrorReport>();

    // NOTE: Users are now accessed via the inherited `Users` DbSet<AppUser> from
    // IdentityDbContext, backed by the standard AspNetUsers/AspNetRoles/AspNetUserRoles
    // tables. Create/update/delete through UserManager<AppUser> and RoleManager<IdentityRole<int>>
    // rather than db.Users.Add/Remove directly, so password hashing and role membership
    // stay consistent with Identity's own bookkeeping.

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
            // Dependencies point at the logical mod (ModGroup), not a specific pinned version —
            // so a dependent always resolves to whatever the dependency's current latest
            // approved version is (see ModsService.ResolveDependenciesAsync), rather than
            // staying locked to whatever version happened to be latest at upload time.
            // Restrict here only blocks deleting a ModGroup entirely while something depends on
            // it — it does NOT block deleting an individual old version anymore, which is the
            // whole point of targeting the group instead of a specific Mod row.
            e.HasMany(m => m.DependencyGroups)
             .WithMany()
             .UsingEntity<Dictionary<string, object>>(
                 "ModDependency",
                 r => r.HasOne<ModGroup>().WithMany().HasForeignKey("DependencyGroupId").OnDelete(DeleteBehavior.Restrict),
                 l => l.HasOne<Mod>().WithMany().HasForeignKey("ModId").OnDelete(DeleteBehavior.Cascade),
                 j =>
                 {
                     j.HasKey("ModId", "DependencyGroupId");
                     j.ToTable("ModDependencies");
                 });
            e.HasMany(m => m.GeneratedPaths)
             .WithOne()
             .HasForeignKey(p => p.ModId)
             .OnDelete(DeleteBehavior.Cascade);
            // Dependencies (List<Mod>) is a resolved, read-only view for display — not persisted.
            e.Ignore(m => m.Dependencies);
        });

        modelBuilder.Entity<ModFile>(e =>
        {
            e.HasKey(f => f.Id);
            e.Property(f => f.FileName).IsRequired();
            e.Property(f => f.InstallPath).HasMaxLength(260);
            e.Property(f => f.Sha256).HasMaxLength(64);
            e.Property(f => f.TargetPath).HasMaxLength(260);
        });

        modelBuilder.Entity<ModFileEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.From).IsRequired().HasMaxLength(512);
            e.Property(x => x.To).IsRequired().HasMaxLength(260);
            e.Property(x => x.Sha256).IsRequired().HasMaxLength(64);
            e.Property(x => x.Kind).HasConversion<string>();
            e.HasIndex(x => x.ModFileId);
            e.HasOne<ModFile>().WithMany().HasForeignKey(x => x.ModFileId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ModGeneratedPath>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Pattern).IsRequired().HasMaxLength(260);
            e.Property(x => x.Kind).HasConversion<string>();
            e.HasIndex(x => x.ModId);
        });

        modelBuilder.Entity<AppUser>(e =>
        {
            e.Property(u => u.F95UserId).HasMaxLength(30);
            e.Property(u => u.F95Username).HasMaxLength(50);
            // Partial unique index: only enforce uniqueness when F95UserId is set.
            e.HasIndex(u => u.F95UserId).IsUnique().HasFilter("\"F95UserId\" IS NOT NULL");
        });

        modelBuilder.Entity<F95PendingVerification>(e =>
        {
            e.HasKey(v => v.Id);
            e.Property(v => v.F95UserId).IsRequired().HasMaxLength(30);
            e.Property(v => v.F95Username).IsRequired().HasMaxLength(50);
            e.Property(v => v.VerificationGuid).IsRequired().HasMaxLength(40);
            e.Property(v => v.ClientTokenHash).IsRequired().HasMaxLength(64);
            e.Property(v => v.Status).HasConversion<string>();
            e.HasIndex(v => v.F95UserId);
            e.HasIndex(v => v.Status);
        });

        // Seed the two baseline roles with fixed IDs so this is deterministic across
        // migrations. Actual users are seeded at runtime in Program.cs (via UserManager,
        // so passwords go through Identity's hasher) rather than here, since HasData
        // requires static, precomputed values and Identity's PasswordHasher salts randomly.
        // ConcurrencyStamp is fixed too: IdentityRole sets a random one in its constructor, which would make every
        // migration rewrite these rows. The values are the ones InitialCreate seeded.
        modelBuilder.Entity<IdentityRole<int>>().HasData(
            new IdentityRole<int> { Id = 1, Name = AppRoles.User, NormalizedName = "USER", ConcurrencyStamp = "69db13d5-503a-4e7d-ba78-0b83959f2d50" },
            new IdentityRole<int> { Id = 2, Name = AppRoles.Admin, NormalizedName = "ADMIN", ConcurrencyStamp = "6013ea0d-1a56-4376-8289-0330a2a31595" }
        );
    }
}
