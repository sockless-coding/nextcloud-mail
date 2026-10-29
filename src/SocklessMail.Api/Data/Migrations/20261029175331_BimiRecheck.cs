using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocklessMail.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class BimiRecheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Verdicts of "no logo" from 0.12.0, which needed an explicit dmarc= result; the sync
            // checks these rows again, now also accepting an aligned DKIM or SPF pass.
            migrationBuilder.Sql("UPDATE messages SET BimiDomain = NULL WHERE BimiDomain = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
