using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace F3M.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddTagSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SiteSettings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteSettings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "TagSuggestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ModGroupId = table.Column<int>(type: "INTEGER", nullable: false),
                    TagId = table.Column<int>(type: "INTEGER", nullable: true),
                    RequestedName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    RequestedNormalizedName = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SuggestedById = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DecidedById = table.Column<int>(type: "INTEGER", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Score = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagSuggestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TagSuggestions_AspNetUsers_DecidedById",
                        column: x => x.DecidedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TagSuggestions_AspNetUsers_SuggestedById",
                        column: x => x.SuggestedById,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_TagSuggestions_ModGroups_ModGroupId",
                        column: x => x.ModGroupId,
                        principalTable: "ModGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TagSuggestions_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TagVotes",
                columns: table => new
                {
                    SuggestionId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagVotes", x => new { x.SuggestionId, x.UserId });
                    table.ForeignKey(
                        name: "FK_TagVotes_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TagVotes_TagSuggestions_SuggestionId",
                        column: x => x.SuggestionId,
                        principalTable: "TagSuggestions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TagSuggestions_DecidedById",
                table: "TagSuggestions",
                column: "DecidedById");

            migrationBuilder.CreateIndex(
                name: "IX_TagSuggestions_ModGroupId_TagId",
                table: "TagSuggestions",
                columns: new[] { "ModGroupId", "TagId" },
                unique: true,
                filter: "\"TagId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TagSuggestions_State_Score",
                table: "TagSuggestions",
                columns: new[] { "State", "Score" });

            migrationBuilder.CreateIndex(
                name: "IX_TagSuggestions_SuggestedById",
                table: "TagSuggestions",
                column: "SuggestedById");

            migrationBuilder.CreateIndex(
                name: "IX_TagSuggestions_TagId",
                table: "TagSuggestions",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_TagVotes_UserId",
                table: "TagVotes",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SiteSettings");

            migrationBuilder.DropTable(
                name: "TagVotes");

            migrationBuilder.DropTable(
                name: "TagSuggestions");
        }
    }
}
