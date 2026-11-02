using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocklessMail.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CryptoKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "crypto_keys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Addresses = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    PublicData = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ProtectedSecret = table.Column<string>(type: "TEXT", nullable: true),
                    HasPassphrase = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    AddedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_crypto_keys", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_crypto_keys_OwnerUserId_Kind_Fingerprint",
                table: "crypto_keys",
                columns: new[] { "OwnerUserId", "Kind", "Fingerprint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "crypto_keys");
        }
    }
}
