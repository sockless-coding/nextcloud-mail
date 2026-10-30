using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocklessMail.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Tags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Keywords",
                table: "messages",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TagStorage",
                table: "accounts",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.CreateTable(
                name: "tagged_messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    FolderName = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    UidValidity = table.Column<uint>(type: "INTEGER", nullable: false),
                    Uid = table.Column<uint>(type: "INTEGER", nullable: false),
                    MessageIdHeader = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Keywords = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    From = table.Column<string>(type: "TEXT", nullable: false),
                    To = table.Column<string>(type: "TEXT", nullable: false),
                    Date = table.Column<long>(type: "INTEGER", nullable: false),
                    Seen = table.Column<bool>(type: "INTEGER", nullable: false),
                    Flagged = table.Column<bool>(type: "INTEGER", nullable: false),
                    Answered = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasAttachments = table.Column<bool>(type: "INTEGER", nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    Preview = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    BimiDomain = table.Column<string>(type: "TEXT", maxLength: 253, nullable: true),
                    BimiSelector = table.Column<string>(type: "TEXT", maxLength: 63, nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tagged_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tagged_messages_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Color = table.Column<string>(type: "TEXT", maxLength: 7, nullable: false),
                    Keyword = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tags", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tagged_messages_AccountId_FolderName_UidValidity_Uid",
                table: "tagged_messages",
                columns: new[] { "AccountId", "FolderName", "UidValidity", "Uid" });

            migrationBuilder.CreateIndex(
                name: "IX_tagged_messages_AccountId_MessageIdHeader",
                table: "tagged_messages",
                columns: new[] { "AccountId", "MessageIdHeader" });

            migrationBuilder.CreateIndex(
                name: "IX_tagged_messages_OwnerUserId_Date",
                table: "tagged_messages",
                columns: new[] { "OwnerUserId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_tags_OwnerUserId_Keyword",
                table: "tags",
                columns: new[] { "OwnerUserId", "Keyword" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tagged_messages");

            migrationBuilder.DropTable(
                name: "tags");

            migrationBuilder.DropColumn(
                name: "Keywords",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "TagStorage",
                table: "accounts");
        }
    }
}
