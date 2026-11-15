using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace F3M.Server.Migrations
{
    /// <inheritdoc />
    public partial class MoveDownloadsToModGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DownloadCount",
                table: "ModGroups",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Downloads are counted per mod now: each mod starts with what its versions had together.
            migrationBuilder.Sql("""
                UPDATE "ModGroups"
                SET "DownloadCount" = (SELECT COALESCE(SUM("DownloadCount"), 0) FROM "Mods" WHERE "Mods"."ModGroupId" = "ModGroups"."Id");
                """);

            migrationBuilder.DropColumn(
                name: "DownloadCount",
                table: "Mods");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DownloadCount",
                table: "Mods",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // The split per version is gone; the latest version gets the whole count.
            migrationBuilder.Sql("""
                UPDATE "Mods"
                SET "DownloadCount" = (SELECT "DownloadCount" FROM "ModGroups" WHERE "ModGroups"."Id" = "Mods"."ModGroupId")
                WHERE "IsLatestVersion" = 1;
                """);

            migrationBuilder.DropColumn(
                name: "DownloadCount",
                table: "ModGroups");
        }
    }
}
