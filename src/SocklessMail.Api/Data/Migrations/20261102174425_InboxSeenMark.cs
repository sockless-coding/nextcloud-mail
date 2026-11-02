using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocklessMail.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InboxSeenMark : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "SeenUid",
                table: "folders",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SeenUid",
                table: "folders");
        }
    }
}
