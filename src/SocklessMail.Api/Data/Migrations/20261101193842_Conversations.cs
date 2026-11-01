using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocklessMail.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Conversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConversationId",
                table: "messages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MessageIdHeader",
                table: "messages",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentIdHeader",
                table: "messages",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Conversations",
                table: "accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "conversation_links",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageIdHeader = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversation_links", x => new { x.AccountId, x.MessageIdHeader });
                    table.ForeignKey(
                        name: "FK_conversation_links_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_messages_ConversationId",
                table: "messages",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_conversation_links_AccountId_ConversationId",
                table: "conversation_links",
                columns: new[] { "AccountId", "ConversationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "conversation_links");

            migrationBuilder.DropIndex(
                name: "IX_messages_ConversationId",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "ConversationId",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "MessageIdHeader",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "ParentIdHeader",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "Conversations",
                table: "accounts");
        }
    }
}
