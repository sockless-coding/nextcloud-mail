using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocklessMail.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrustedSenders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "trusted_senders",
                columns: table => new
                {
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Domain = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trusted_senders", x => new { x.OwnerUserId, x.Domain });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trusted_senders");
        }
    }
}
