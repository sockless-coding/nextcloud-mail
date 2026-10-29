using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocklessMail.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BimiLogos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BimiDomain",
                table: "messages",
                type: "TEXT",
                maxLength: 253,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BimiSelector",
                table: "messages",
                type: "TEXT",
                maxLength: 63,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "bimi_logos",
                columns: table => new
                {
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Svg = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bimi_logos", x => x.Hash);
                });

            migrationBuilder.CreateTable(
                name: "bimi_lookups",
                columns: table => new
                {
                    Domain = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    Selector = table.Column<string>(type: "TEXT", maxLength: 63, nullable: false),
                    LogoHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CheckedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bimi_lookups", x => new { x.Domain, x.Selector });
                });

            migrationBuilder.CreateIndex(
                name: "IX_bimi_lookups_ExpiresAt",
                table: "bimi_lookups",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_bimi_lookups_LogoHash",
                table: "bimi_lookups",
                column: "LogoHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bimi_logos");

            migrationBuilder.DropTable(
                name: "bimi_lookups");

            migrationBuilder.DropColumn(
                name: "BimiDomain",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "BimiSelector",
                table: "messages");
        }
    }
}
