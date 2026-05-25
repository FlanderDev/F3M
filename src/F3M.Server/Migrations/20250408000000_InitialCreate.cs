using Microsoft.EntityFrameworkCore.Migrations;

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
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("ModFiles");
        migrationBuilder.DropTable("Mods");
        migrationBuilder.DropTable("ModGroups");
        migrationBuilder.DropTable("Users");
    }
}
