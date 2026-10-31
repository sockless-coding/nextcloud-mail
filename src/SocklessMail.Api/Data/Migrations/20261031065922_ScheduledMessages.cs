using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocklessMail.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class ScheduledMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "scheduled_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SendAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DueAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Recipients = table.Column<string>(type: "TEXT", nullable: false),
                    Preview = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    HasAttachments = table.Column<bool>(type: "INTEGER", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    MessageIdHeader = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    SourceFolderId = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    SourceMessageId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SourceMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scheduled_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_scheduled_messages_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_messages_AccountId",
                table: "scheduled_messages",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_messages_OwnerUserId_SendAt",
                table: "scheduled_messages",
                columns: new[] { "OwnerUserId", "SendAt" });

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_messages_Status_DueAt",
                table: "scheduled_messages",
                columns: new[] { "Status", "DueAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "scheduled_messages");
        }
    }
}
