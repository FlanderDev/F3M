using F3M.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace F3M.Server.Migrations;

[DbContext(typeof(AppDbContext))]
partial class AppDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "9.0.4");

        modelBuilder.Entity("F3M.Shared.Models.AppUser", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
            b.Property<string>("Username").IsRequired().HasMaxLength(50).HasColumnType("TEXT");
            b.Property<string>("Email").HasMaxLength(200).HasColumnType("TEXT");
            b.Property<string>("PasswordHash").IsRequired().HasColumnType("TEXT");
            b.Property<DateTime>("RegisteredAt").HasColumnType("TEXT");
            b.Property<bool>("IsAdmin").HasDefaultValue(false).HasColumnType("INTEGER");
            b.Property<string>("F95UserId").HasMaxLength(30).HasColumnType("TEXT");
            b.Property<string>("F95Username").HasMaxLength(50).HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("Username").IsUnique();
            b.HasIndex("F95UserId").IsUnique().HasFilter("\"F95UserId\" IS NOT NULL");
            b.ToTable("Users");
        });

        modelBuilder.Entity("F3M.Server.Models.F95PendingVerification", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
            b.Property<string>("F95UserId").IsRequired().HasMaxLength(30).HasColumnType("TEXT");
            b.Property<string>("F95Username").IsRequired().HasMaxLength(50).HasColumnType("TEXT");
            b.Property<string>("VerificationGuid").IsRequired().HasMaxLength(40).HasColumnType("TEXT");
            b.Property<long>("ProfilePostId").HasColumnType("INTEGER");
            b.Property<DateTime>("CreatedAt").HasColumnType("TEXT");
            b.Property<string>("Status").IsRequired().HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("F95UserId");
            b.HasIndex("Status");
            b.ToTable("F95PendingVerifications");
        });

        modelBuilder.Entity("F3M.Shared.Models.ModGroup", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
            b.Property<int?>("OwnerId").HasColumnType("INTEGER");
            b.Property<string>("Author").IsRequired().HasMaxLength(80).HasColumnType("TEXT");
            b.HasKey("Id");
            b.ToTable("ModGroups");
        });

        modelBuilder.Entity("F3M.Shared.Models.Mod", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
            b.Property<int>("ModGroupId").HasColumnType("INTEGER");
            b.Property<string>("Author").IsRequired().HasMaxLength(80).HasColumnType("TEXT");
            b.Property<string>("Category").IsRequired().HasMaxLength(50).HasColumnType("TEXT");
            b.Property<string>("Description").IsRequired().HasMaxLength(1000).HasColumnType("TEXT");
            b.Property<int>("DownloadCount").HasColumnType("INTEGER");
            b.Property<string>("PreviewImageName").HasColumnType("TEXT");
            b.Property<bool>("IsApproved").HasColumnType("INTEGER");
            b.Property<bool>("IsLatestVersion").HasColumnType("INTEGER");
            b.Property<string>("Name").IsRequired().HasMaxLength(120).HasColumnType("TEXT");
            b.Property<DateTime>("UploadedAt").HasColumnType("TEXT");
            b.Property<int?>("UserId").HasColumnType("INTEGER");
            b.Property<string>("Version").IsRequired().HasMaxLength(20).HasColumnType("TEXT");
            b.HasKey("Id");
            b.HasIndex("Category");
            b.HasIndex("Name");
            b.HasIndex("ModGroupId");
            b.HasIndex("IsLatestVersion");
            b.ToTable("Mods");
        });

        modelBuilder.Entity("ModDependency", b =>
        {
            b.Property<int>("ModId").HasColumnType("INTEGER");
            b.Property<int>("DependencyId").HasColumnType("INTEGER");
            b.HasKey("ModId", "DependencyId");
            b.HasIndex("DependencyId");
            b.ToTable("ModDependencies");
        });

        modelBuilder.Entity("F3M.Shared.Models.ModFile", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
            b.Property<int>("ModId").HasColumnType("INTEGER");
            b.Property<string>("FileName").IsRequired().HasColumnType("TEXT");
            b.Property<string>("OriginalName").IsRequired().HasColumnType("TEXT");
            b.Property<string>("InstallPath").IsRequired().HasMaxLength(260).HasColumnType("TEXT");
            b.Property<long>("FileSizeBytes").HasColumnType("INTEGER");
            b.HasKey("Id");
            b.HasIndex("ModId");
            b.ToTable("ModFiles");
        });

        modelBuilder.Entity("ModDependency", b =>
        {
            b.HasOne("F3M.Shared.Models.Mod", null)
             .WithMany()
             .HasForeignKey("ModId")
             .OnDelete(DeleteBehavior.Cascade)
             .IsRequired();
            b.HasOne("F3M.Shared.Models.Mod", null)
             .WithMany()
             .HasForeignKey("DependencyId")
             .OnDelete(DeleteBehavior.Restrict)
             .IsRequired();
        });

        modelBuilder.Entity("F3M.Shared.Models.ModFile", b2 =>
        {
            b2.HasOne("F3M.Shared.Models.Mod", null)
              .WithMany("Files")
              .HasForeignKey("ModId")
              .OnDelete(DeleteBehavior.Cascade)
              .IsRequired();
        });
    }
}
