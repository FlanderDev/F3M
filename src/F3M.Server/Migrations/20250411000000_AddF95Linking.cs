using Microsoft.EntityFrameworkCore.Migrations;

namespace F3M.Server.Migrations;

public partial class AddF95Linking : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Drop the email unique index — F95-linked accounts have no email.
        migrationBuilder.DropIndex(
            name:  "IX_Users_Email",
            table: "Users");

        // Add F95 linking columns to Users.
        migrationBuilder.AddColumn<string>(
            name:      "F95UserId",
            table:     "Users",
            type:      "TEXT",
            maxLength: 30,
            nullable:  true);

        migrationBuilder.AddColumn<string>(
            name:      "F95Username",
            table:     "Users",
            type:      "TEXT",
            maxLength: 50,
            nullable:  true);

        // Partial unique index on F95UserId (NULL rows are excluded by SQLite).
        migrationBuilder.CreateIndex(
            name:    "IX_Users_F95UserId",
            table:   "Users",
            column:  "F95UserId",
            unique:  true,
            filter:  "\"F95UserId\" IS NOT NULL");

        // Pending F95 verification table.
        migrationBuilder.CreateTable(
            name: "F95PendingVerifications",
            columns: table => new
            {
                Id               = table.Column<int>("INTEGER",  nullable: false)
                                        .Annotation("Sqlite:Autoincrement", true),
                F95UserId        = table.Column<string>("TEXT",  maxLength: 30,  nullable: false),
                F95Username      = table.Column<string>("TEXT",  maxLength: 50,  nullable: false),
                VerificationGuid = table.Column<string>("TEXT",  maxLength: 40,  nullable: false),
                ProfilePostId    = table.Column<long>("INTEGER",               nullable: false),
                CreatedAt        = table.Column<DateTime>("TEXT",              nullable: false),
                Status           = table.Column<string>("TEXT",               nullable: false,
                                       defaultValue: "Pending")
            },
            constraints: t => t.PrimaryKey("PK_F95PendingVerifications", x => x.Id));

        migrationBuilder.CreateIndex(
            name:   "IX_F95PendingVerifications_F95UserId",
            table:  "F95PendingVerifications",
            column: "F95UserId");

        migrationBuilder.CreateIndex(
            name:   "IX_F95PendingVerifications_Status",
            table:  "F95PendingVerifications",
            column: "Status");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("F95PendingVerifications");

        migrationBuilder.DropIndex("IX_Users_F95UserId", "Users");
        migrationBuilder.DropColumn("F95UserId", "Users");
        migrationBuilder.DropColumn("F95Username", "Users");

        // Restore the email unique index.
        migrationBuilder.CreateIndex(
            name:   "IX_Users_Email",
            table:  "Users",
            column: "Email",
            unique: true);
    }
}
