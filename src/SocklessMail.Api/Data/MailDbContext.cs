using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SocklessMail.Api.Accounts;

namespace SocklessMail.Api.Data;

/// <summary>
/// The app database (SQLite in the persistent volume). Every user-owned entity carries a query
/// filter on the current Nextcloud user, as a second line of defence behind the explicit owner
/// checks in the services: a query that forgets to scope still cannot see another user's rows.
/// Without a user (startup, background work) the filter matches nothing; such code must use
/// IgnoreQueryFilters deliberately.
/// </summary>
public sealed class MailDbContext(DbContextOptions<MailDbContext> options, ICurrentUser currentUser) : DbContext(options)
{
    private readonly string? _userId = currentUser.UserId;

    public DbSet<MailAccount> Accounts => Set<MailAccount>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<MailAccount>(account =>
        {
            account.ToTable("accounts");
            account.HasKey(a => a.Id);
            account.HasIndex(a => new { a.OwnerUserId, a.SortOrder });
            account.Property(a => a.OwnerUserId).HasMaxLength(64);
            account.Property(a => a.DisplayName).HasMaxLength(200);
            account.Property(a => a.SenderName).HasMaxLength(200);
            account.Property(a => a.EmailAddress).HasMaxLength(320);
            account.Property(a => a.ImapHost).HasMaxLength(253);
            account.Property(a => a.SmtpHost).HasMaxLength(253);
            account.Property(a => a.Username).HasMaxLength(320);
            account.Property(a => a.ImapSecurity).HasConversion<string>().HasMaxLength(16);
            account.Property(a => a.SmtpSecurity).HasConversion<string>().HasMaxLength(16);
            account.Property(a => a.AuthMethod).HasConversion<string>().HasMaxLength(16);
            // SQLite cannot order or compare DateTimeOffset natively; store UTC ticks.
            account.Property(a => a.CreatedAt).HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
            account.Property(a => a.UpdatedAt).HasConversion(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));
            account.HasQueryFilter(a => a.OwnerUserId == _userId);
        });
    }
}

/// <summary>For `dotnet ef migrations`; never used at runtime.</summary>
internal sealed class DesignTimeMailDbContextFactory : IDesignTimeDbContextFactory<MailDbContext>
{
    public MailDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<MailDbContext>().UseSqlite("Data Source=design-time.db").Options, new NoUser());

    private sealed class NoUser : ICurrentUser
    {
        public string? UserId => null;
    }
}
