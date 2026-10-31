using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SocklessMail.Api.Data.Migrations
{
    /// <summary>
    /// Folder lists are ordered by date now, and a window must hold every message as new as the
    /// ones it caches (see FolderCache.CoverByDateAsync). Windows built before that may not: each
    /// folder rebuilds its window on its next sync.
    /// </summary>
    public partial class RebuildWindowsByDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE folders SET WindowStart = NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
