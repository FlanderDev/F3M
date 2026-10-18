using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace F3M.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddTags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    TypeId = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ModGroupTags",
                columns: table => new
                {
                    ModGroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    TagId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModGroupTags", x => new { x.ModGroupId, x.TagId });
                    table.ForeignKey(
                        name: "FK_ModGroupTags_ModGroups_ModGroupId",
                        column: x => x.ModGroupId,
                        principalTable: "ModGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ModGroupTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TagInclusions",
                columns: table => new
                {
                    ParentId = table.Column<int>(type: "INTEGER", nullable: false),
                    ChildId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagInclusions", x => new { x.ParentId, x.ChildId });
                    table.ForeignKey(
                        name: "FK_TagInclusions_Tags_ChildId",
                        column: x => x.ChildId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TagInclusions_Tags_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ModGroupTags_TagId",
                table: "ModGroupTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_TagInclusions_ChildId",
                table: "TagInclusions",
                column: "ChildId");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_NormalizedName",
                table: "Tags",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tags_TypeId",
                table: "Tags",
                column: "TypeId",
                unique: true,
                filter: "\"TypeId\" IS NOT NULL");

            // ── Categories become tags ──
            // The built-in tag of each mod type (TagService.EnsureTypeTagsAsync adds types that come later). The values
            // are fixed here, as they were when this migration was written: id, tag name, description, old category.
            (string TypeId, string Name, string Description, string Category)[] types =
            [
                ("bepinex-plugin", "BepInEx plugin", "Plugins (.dll files) for BepInEx.", "BepInEx-Plugin"),
                ("custom-missions-v1", "Custom Missions v1", "Missions for the Custom Missions v1 framework.", "Custom-Missions 1"),
                ("custom-missions-v2", "Custom Missions v2", "Missions for the Custom Missions v2 framework.", "Custom-Missions 2"),
                ("cosplay", "Cosplay", "Outfits for the Cosplay Loader.", "Cosplay-Loader"),
                ("texture-pack", "Texture pack", "Textures for Texture Replacer.", "Texture Edits"),
            ];
            foreach (var (typeId, name, description, _) in types)
            {
                migrationBuilder.Sql($"""
                    INSERT INTO "Tags" ("Name", "NormalizedName", "Description", "TypeId", "CreatedAt")
                    VALUES ('{name}', '{name.ToUpperInvariant()}', '{description}', '{typeId}', strftime('%Y-%m-%d %H:%M:%S', 'now'));
                    """);
            }

            // Every mod gets the type tags of its latest version's files.
            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO "ModGroupTags" ("ModGroupId", "TagId")
                SELECT DISTINCT m."ModGroupId", t."Id"
                FROM "Mods" m
                JOIN "ModFiles" f ON f."ModId" = m."Id"
                JOIN "Tags" t ON t."TypeId" = f."Template"
                WHERE m."IsLatestVersion" = 1;
                """);

            // A latest version without typed files keeps the type its category named (the frameworks, for example, were
            // uploaded as "custom" files under BepInEx-Plugin). TagService.SyncTypeTagsAsync does the same from now on.
            var categoryTypes = string.Join(", ", types.Select(t => $"('{t.Category.ToUpperInvariant()}', '{t.TypeId}')"));
            migrationBuilder.Sql($"""
                WITH "CategoryTypes" ("Category", "TypeId") AS (VALUES {categoryTypes})
                INSERT OR IGNORE INTO "ModGroupTags" ("ModGroupId", "TagId")
                SELECT m."ModGroupId", t."Id"
                FROM "Mods" m
                JOIN "CategoryTypes" c ON c."Category" = upper(trim(m."Category"))
                JOIN "Tags" t ON t."TypeId" = c."TypeId"
                WHERE m."IsLatestVersion" = 1
                  AND NOT EXISTS (SELECT 1 FROM "ModFiles" f JOIN "Tags" ft ON ft."TypeId" = f."Template" WHERE f."ModId" = m."Id");
                """);

            // Any other category becomes an ordinary tag on its mods. "Others" and the placeholders mean none.
            var notTags = string.Join(", ", types.Select(t => $"'{t.Category.ToUpperInvariant()}'").Concat(["'OTHERS'", "'UNSPECIFIED'", "'GENERAL'", "''"]));
            migrationBuilder.Sql($"""
                INSERT OR IGNORE INTO "Tags" ("Name", "NormalizedName", "CreatedAt")
                SELECT DISTINCT trim(substr(m."Category", 1, 40)), upper(trim(substr(m."Category", 1, 40))), strftime('%Y-%m-%d %H:%M:%S', 'now')
                FROM "Mods" m
                WHERE m."IsLatestVersion" = 1 AND upper(trim(m."Category")) NOT IN ({notTags});
                """);
            migrationBuilder.Sql($"""
                INSERT OR IGNORE INTO "ModGroupTags" ("ModGroupId", "TagId")
                SELECT m."ModGroupId", t."Id"
                FROM "Mods" m
                JOIN "Tags" t ON t."NormalizedName" = upper(trim(substr(m."Category", 1, 40))) AND t."TypeId" IS NULL
                WHERE m."IsLatestVersion" = 1 AND upper(trim(m."Category")) NOT IN ({notTags});
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModGroupTags");

            migrationBuilder.DropTable(
                name: "TagInclusions");

            migrationBuilder.DropTable(
                name: "Tags");
        }
    }
}
