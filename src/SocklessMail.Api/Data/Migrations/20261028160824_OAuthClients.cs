using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocklessMail.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class OAuthClients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "oauth_clients",
                columns: table => new
                {
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ClientId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ProtectedClientSecret = table.Column<string>(type: "TEXT", nullable: false),
                    Tenant = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RedirectUri = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oauth_clients", x => x.ProviderId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "oauth_clients");
        }
    }
}
