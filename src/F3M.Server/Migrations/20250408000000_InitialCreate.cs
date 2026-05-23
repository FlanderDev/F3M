using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1814

namespace F3M.Server.Migrations;

public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Users
        migrationBuilder.CreateTable("Users", table => new
        {
            Id           = table.Column<int>("INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
            Username     = table.Column<string>("TEXT", maxLength: 50,  nullable: false),
            Email        = table.Column<string>("TEXT", maxLength: 200, nullable: false),
            PasswordHash = table.Column<string>("TEXT", nullable: false),
            RegisteredAt = table.Column<DateTime>("TEXT", nullable: false)
        }, constraints: t => t.PrimaryKey("PK_Users", x => x.Id));
        migrationBuilder.CreateIndex("IX_Users_Username", "Users", "Username", unique: true);
        migrationBuilder.CreateIndex("IX_Users_Email",    "Users", "Email",    unique: true);

        // ModGroups
        migrationBuilder.CreateTable("ModGroups", table => new
        {
            Id      = table.Column<int>("INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
            OwnerId = table.Column<int>("INTEGER", nullable: true),
            Author  = table.Column<string>("TEXT", maxLength: 80, nullable: false)
        }, constraints: t => t.PrimaryKey("PK_ModGroups", x => x.Id));

        // Mods (version records)
        migrationBuilder.CreateTable("Mods", table => new
        {
            Id               = table.Column<int>("INTEGER",  nullable: false).Annotation("Sqlite:Autoincrement", true),
            ModGroupId       = table.Column<int>("INTEGER",  nullable: false),
            Name             = table.Column<string>("TEXT",  maxLength: 120,  nullable: false),
            Description      = table.Column<string>("TEXT",  maxLength: 1000, nullable: false),
            Author           = table.Column<string>("TEXT",  maxLength: 80,   nullable: false),
            Version          = table.Column<string>("TEXT",  maxLength: 20,   nullable: false),
            Category         = table.Column<string>("TEXT",  maxLength: 50,   nullable: false),
            PreviewImageName = table.Column<string>("TEXT",  nullable: true),
            DownloadCount    = table.Column<int>("INTEGER",  nullable: false),
            UploadedAt       = table.Column<DateTime>("TEXT",nullable: false),
            IsApproved       = table.Column<bool>("INTEGER", nullable: false),
            UserId           = table.Column<int>("INTEGER",  nullable: true)
        }, constraints: t => t.PrimaryKey("PK_Mods", x => x.Id));
        migrationBuilder.CreateIndex("IX_Mods_Name",       "Mods", "Name");
        migrationBuilder.CreateIndex("IX_Mods_Category",   "Mods", "Category");
        migrationBuilder.CreateIndex("IX_Mods_ModGroupId", "Mods", "ModGroupId");

        // ModFiles
        migrationBuilder.CreateTable("ModFiles", table => new
        {
            Id            = table.Column<int>("INTEGER",  nullable: false).Annotation("Sqlite:Autoincrement", true),
            ModId         = table.Column<int>("INTEGER",  nullable: false),
            FileName      = table.Column<string>("TEXT",  nullable: false),
            OriginalName  = table.Column<string>("TEXT",  nullable: false),
            InstallPath   = table.Column<string>("TEXT",  maxLength: 260, nullable: false),
            FileSizeBytes = table.Column<long>("INTEGER", nullable: false)
        }, constraints: t => {
            t.PrimaryKey("PK_ModFiles", x => x.Id);
            t.ForeignKey("FK_ModFiles_Mods_ModId", x => x.ModId, "Mods", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_ModFiles_ModId", "ModFiles", "ModId");

        // ── Seed ──────────────────────────────────────────────────────────
        migrationBuilder.InsertData("ModGroups",
            new[] { "Id","Author","OwnerId" },
            new object[,] {
                { 1, "pixelcraft", null },
                { 2, "soundforge", null },
                { 3, "weathermod", null },
                { 4, "itemsmith",  null },
                { 5, "soundforge", null },
                { 6, "uimod",      null }
            });

        migrationBuilder.InsertData("Mods",
            new[] { "Id","ModGroupId","Author","Category","Description","DownloadCount","PreviewImageName","IsApproved","Name","UploadedAt","UserId","Version" },
            new object[,] {
                { 1, 1, "pixelcraft", "Textures", "Complete high-resolution texture replacement for all environments. Supports 2K and 4K output.", 3891, null, true, "HD Texture Overhaul",    new DateTime(2025,2,5, 0,0,0,DateTimeKind.Utc), null, "1.0.0" },
                { 2, 2, "soundforge", "Audio",    "Adds rich ambient soundscapes to all outdoor zones. Includes dynamic weather audio layers.",    1420, null, true, "Ambient Sound Pack",     new DateTime(2025,1,10,0,0,0,DateTimeKind.Utc), null, "2.1.0" },
                { 3, 3, "weathermod", "Gameplay", "Realistic weather system with rain, fog, storms and seasonal cycles. Fully configurable.",      762,  null, true, "Dynamic Weather System", new DateTime(2025,3,1, 0,0,0,DateTimeKind.Utc), null, "0.9.5" },
                { 4, 4, "itemsmith",  "Items",    "Adds 200+ new craftable items, weapons and consumables with balanced progression.",             2105, null, true, "Extended Item Pack",     new DateTime(2025,3,18,0,0,0,DateTimeKind.Utc), null, "1.1.0" },
                { 5, 5, "soundforge", "Audio",    "Full OST replacement featuring an original orchestral score. 3 hours of new music.",           504,  null, true, "Orchestral Soundtrack",  new DateTime(2025,4,2, 0,0,0,DateTimeKind.Utc), null, "3.0.0" },
                { 6, 6, "uimod",      "UI",       "Clean, modern HUD redesign with better inventory management and readable fonts.",              3100, null, true, "UI Overhaul",            new DateTime(2025,4,5, 0,0,0,DateTimeKind.Utc), null, "1.5.2" }
            });

        migrationBuilder.InsertData("ModFiles",
            new[] { "Id","ModId","FileName","OriginalName","InstallPath","FileSizeBytes" },
            new object[,] {
                { 1, 1, "seed_hd_textures.zip",  "hd_textures_v1.0.zip",     "Manaka/Mods/Textures", 512000000L },
                { 2, 2, "seed_ambient.zip",       "ambient_sounds_v2.1.zip",   "Manaka/Mods/Audio",    48000000L  },
                { 3, 3, "seed_weather.zip",        "weather_system_v0.9.5.zip","Manaka/Mods/Gameplay", 8200000L   },
                { 4, 4, "seed_items.zip",          "item_pack_v1.1.zip",       "Manaka/Mods/Items",    3400000L   },
                { 5, 5, "seed_ost.zip",            "ost_orchestral_v3.0.zip",  "Manaka/Mods/Audio",    220000000L },
                { 6, 6, "seed_ui.zip",             "ui_overhaul_v1.5.2.zip",   "Manaka/Mods/UI",       1800000L   }
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("ModFiles");
        migrationBuilder.DropTable("Mods");
        migrationBuilder.DropTable("ModGroups");
        migrationBuilder.DropTable("Users");
    }
}
