using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocklessMail.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class MailboxCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "folders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FullName = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    UidValidity = table.Column<uint>(type: "INTEGER", nullable: false),
                    HighestModSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    WindowStart = table.Column<uint>(type: "INTEGER", nullable: true),
                    LastUid = table.Column<uint>(type: "INTEGER", nullable: false),
                    UidNext = table.Column<uint>(type: "INTEGER", nullable: true),
                    Total = table.Column<int>(type: "INTEGER", nullable: false),
                    Unread = table.Column<int>(type: "INTEGER", nullable: true),
                    ChangeSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    ResetSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    SyncedAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_folders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_folders_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    FolderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Uid = table.Column<uint>(type: "INTEGER", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    From = table.Column<string>(type: "TEXT", nullable: false),
                    To = table.Column<string>(type: "TEXT", nullable: false),
                    Date = table.Column<long>(type: "INTEGER", nullable: false),
                    HasAttachments = table.Column<bool>(type: "INTEGER", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Flags = table.Column<int>(type: "INTEGER", nullable: false),
                    ChangeSeq = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messages", x => new { x.FolderId, x.Uid });
                    table.ForeignKey(
                        name: "FK_messages_folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "removed_messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FolderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Uid = table.Column<uint>(type: "INTEGER", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ChangeSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    RemovedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_removed_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_removed_messages_folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_folders_AccountId_FullName",
                table: "folders",
                columns: new[] { "AccountId", "FullName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_messages_FolderId_ChangeSeq",
                table: "messages",
                columns: new[] { "FolderId", "ChangeSeq" });

            migrationBuilder.CreateIndex(
                name: "IX_removed_messages_FolderId_ChangeSeq",
                table: "removed_messages",
                columns: new[] { "FolderId", "ChangeSeq" });

            migrationBuilder.CreateIndex(
                name: "IX_removed_messages_RemovedAt",
                table: "removed_messages",
                column: "RemovedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "messages");

            migrationBuilder.DropTable(
                name: "removed_messages");

            migrationBuilder.DropTable(
                name: "folders");
        }
    }
}
