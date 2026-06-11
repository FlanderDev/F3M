using Microsoft.EntityFrameworkCore.Migrations;

namespace F3M.Server.Migrations;

public partial class AddModDependencies : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ModDependencies",
            columns: table => new
            {
                ModId = table.Column<int>("INTEGER", nullable: false),
                DependencyId = table.Column<int>("INTEGER", nullable: false)
            },
            constraints: t =>
            {
                t.PrimaryKey("PK_ModDependencies", x => new { x.ModId, x.DependencyId });
                t.ForeignKey(
                    name: "FK_ModDependencies_Mods_ModId",
                    column: x => x.ModId,
                    principalTable: "Mods",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                t.ForeignKey(
                    name: "FK_ModDependencies_Mods_DependencyId",
                    column: x => x.DependencyId,
                    principalTable: "Mods",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ModDependencies_DependencyId",
            table: "ModDependencies",
            column: "DependencyId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ModDependencies");
    }
}
