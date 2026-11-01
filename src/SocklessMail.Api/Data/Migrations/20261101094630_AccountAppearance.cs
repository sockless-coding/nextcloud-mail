using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocklessMail.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccountAppearance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BadgeIcon",
                table: "accounts",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BadgeText",
                table: "accounts",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Color",
                table: "accounts",
                type: "TEXT",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IncludeInUnified",
                table: "accounts",
                type: "INTEGER",
                nullable: false,
                // Existing accounts stay in "All accounts".
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BadgeIcon",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "BadgeText",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "Color",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "IncludeInUnified",
                table: "accounts");
        }
    }
}
