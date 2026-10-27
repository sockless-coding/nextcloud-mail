using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace SocklessMail.Api.Data;

public static class DatabaseSetup
{
    public static IServiceCollection AddMailDatabase(this IServiceCollection services, SocklessMailOptions options)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();
        services.AddDbContext<MailDbContext>(db => db.UseSqlite(connectionString));
        return services;
    }

    /// <summary>Applies migrations before the server starts listening, so there is no /init work.</summary>
    public static async Task MigrateMailDatabaseAsync(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<SocklessMailOptions>();
        Directory.CreateDirectory(options.StorageDirectory);

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
        await db.Database.MigrateAsync();
        // Readers do not block the writer; persistent for the database file.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    }
}
