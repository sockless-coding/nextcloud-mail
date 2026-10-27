using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocklessMail.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MessageSearchText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The cache is disposable: drop it so every row is rebuilt with its search text.
            migrationBuilder.Sql("DELETE FROM removed_messages; DELETE FROM messages; DELETE FROM folders;");
            migrationBuilder.AddColumn<string>(
                name: "SearchText",
                table: "messages",
                type: "TEXT",
                maxLength: 4000,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SearchText",
                table: "messages");
        }
    }
}
