using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocklessMail.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ViewStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "account_view_states",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Expanded = table.Column<string>(type: "TEXT", nullable: false),
                    LastFolderId = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_account_view_states", x => x.AccountId);
                    table.ForeignKey(
                        name: "FK_account_view_states_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "view_settings",
                columns: table => new
                {
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Storage = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    LastView = table.Column<string>(type: "TEXT", maxLength: 5000, nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_view_settings", x => x.OwnerUserId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_account_view_states_OwnerUserId",
                table: "account_view_states",
                column: "OwnerUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_view_states");

            migrationBuilder.DropTable(
                name: "view_settings");
        }
    }
}
