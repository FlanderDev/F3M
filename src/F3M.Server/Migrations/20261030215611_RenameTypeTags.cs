using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace F3M.Server.Migrations
{
    /// <summary>
    /// The Cosplay and Texture pack types are named after the mod they need now, like the others: Cosplay Loader and
    /// Texture Replacer. Their tags follow, unless an admin renamed them already or another tag has the new name.
    /// </summary>
    public partial class RenameTypeTags : Migration
    {
        private static readonly (string TypeId, string Old, string New)[] Renames =
        [
            ("cosplay", "Cosplay", "Cosplay Loader"),
            ("texture-pack", "Texture pack", "Texture Replacer"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (typeId, old, name) in Renames)
                Rename(migrationBuilder, typeId, old, name);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (typeId, old, name) in Renames)
                Rename(migrationBuilder, typeId, name, old);
        }

        private static void Rename(MigrationBuilder migrationBuilder, string typeId, string from, string to) =>
            migrationBuilder.Sql($"""
                UPDATE "Tags" SET "Name" = '{to}', "NormalizedName" = '{to.ToUpperInvariant()}'
                WHERE "TypeId" = '{typeId}' AND "Name" = '{from}'
                  AND NOT EXISTS (SELECT 1 FROM "Tags" AS "Other" WHERE "Other"."NormalizedName" = '{to.ToUpperInvariant()}');
                """);
    }
}
