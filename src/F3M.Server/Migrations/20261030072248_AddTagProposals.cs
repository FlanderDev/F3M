using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace F3M.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddTagProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsProposed",
                table: "Tags",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ProposalScore",
                table: "Tags",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // SQLite adds a column with its foreign key in place. AddForeignKey would rebuild the table, outside the
            // migration's transaction.
            migrationBuilder.Sql(
                "ALTER TABLE \"Tags\" ADD COLUMN \"ProposedById\" INTEGER NULL " +
                "CONSTRAINT \"FK_Tags_AspNetUsers_ProposedById\" REFERENCES \"AspNetUsers\" (\"Id\") ON DELETE SET NULL;");

            migrationBuilder.CreateTable(
                name: "TagProposalVotes",
                columns: table => new
                {
                    TagId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagProposalVotes", x => new { x.TagId, x.UserId });
                    table.ForeignKey(
                        name: "FK_TagProposalVotes_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TagProposalVotes_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tags_ProposedById",
                table: "Tags",
                column: "ProposedById");

            migrationBuilder.CreateIndex(
                name: "IX_TagProposalVotes_UserId",
                table: "TagProposalVotes",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tags_AspNetUsers_ProposedById",
                table: "Tags");

            migrationBuilder.DropTable(
                name: "TagProposalVotes");

            migrationBuilder.DropIndex(
                name: "IX_Tags_ProposedById",
                table: "Tags");

            migrationBuilder.DropColumn(
                name: "IsProposed",
                table: "Tags");

            migrationBuilder.DropColumn(
                name: "ProposalScore",
                table: "Tags");

            migrationBuilder.DropColumn(
                name: "ProposedById",
                table: "Tags");
        }
    }
}
