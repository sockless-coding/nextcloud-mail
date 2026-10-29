using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Bimi;
using SocklessMail.Api.Mailbox;
using SocklessMail.Api.OAuth;
using SocklessMail.Api.Sync;

namespace SocklessMail.Api.Data;

/// <summary>
/// The app database (SQLite in the persistent volume). Every user-owned entity carries a query
/// filter on the current Nextcloud user, as a second line of defence behind the explicit owner
/// checks in the services: a query that forgets to scope still cannot see another user's rows.
/// Without a user (startup, background work) the filter matches nothing; background work acts
/// for a user explicitly (<see cref="CurrentUserScopes.CreateUserScope"/>) or uses
/// IgnoreQueryFilters deliberately.
/// </summary>
public sealed class MailDbContext(DbContextOptions<MailDbContext> options, ICurrentUser currentUser) : DbContext(options)
{
    private readonly string? _userId = currentUser.UserId;

    public DbSet<MailAccount> Accounts => Set<MailAccount>();
    public DbSet<CachedFolder> Folders => Set<CachedFolder>();
    public DbSet<CachedMessage> Messages => Set<CachedMessage>();
    public DbSet<RemovedMessage> RemovedMessages => Set<RemovedMessage>();

    /// <summary>Instance-wide OAuth app registrations (admin settings); not user-owned, so not filtered.</summary>
    public DbSet<OAuthClient> OAuthClients => Set<OAuthClient>();

    /// <summary>The BIMI cache: public DNS and web content shared by all users, so not filtered.</summary>
    public DbSet<BimiLookup> BimiLookups => Set<BimiLookup>();

    public DbSet<BimiLogo> BimiLogos => Set<BimiLogo>();

    // SQLite cannot order or compare DateTimeOffset natively; store UTC ticks.
    private static readonly ValueConverter<DateTimeOffset, long> UtcTicks =
        new(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero));

    private static readonly ValueConverter<AddressDto[], string> Addresses =
        new(v => JsonSerializer.Serialize(v, JsonSerializerOptions.Web), v => JsonSerializer.Deserialize<AddressDto[]>(v, JsonSerializerOptions.Web) ?? Array.Empty<AddressDto>());

    private static readonly ValueComparer<AddressDto[]> AddressComparer =
        new((a, b) => a!.SequenceEqual(b!), v => v.Aggregate(0, (hash, address) => HashCode.Combine(hash, address)), v => v.ToArray());

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
            account.Property(a => a.CreatedAt).HasConversion(UtcTicks);
            account.Property(a => a.UpdatedAt).HasConversion(UtcTicks);
            account.HasQueryFilter(a => a.OwnerUserId == _userId);
        });

        model.Entity<CachedFolder>(folder =>
        {
            folder.ToTable("folders");
            folder.HasKey(f => f.Id);
            folder.HasIndex(f => new { f.AccountId, f.FullName }).IsUnique();
            // Deleting an account drops its cache with it.
            folder.HasOne<MailAccount>().WithMany().HasForeignKey(f => f.AccountId).OnDelete(DeleteBehavior.Cascade);
            folder.Property(f => f.OwnerUserId).HasMaxLength(64);
            folder.Property(f => f.FullName).HasMaxLength(1000);
            // Mod-sequences are 63-bit (RFC 7162), so they fit SQLite's signed integer.
            folder.Property(f => f.HighestModSeq).HasConversion<long>();
            folder.Property(f => f.SyncedAt).HasConversion(UtcTicks);
            folder.HasQueryFilter(f => f.OwnerUserId == _userId);
        });

        model.Entity<CachedMessage>(message =>
        {
            message.ToTable("messages");
            message.HasKey(m => new { m.FolderId, m.Uid });
            message.HasIndex(m => new { m.FolderId, m.ChangeSeq });
            message.HasIndex(m => new { m.FolderId, m.Date });
            message.HasOne<CachedFolder>().WithMany().HasForeignKey(m => m.FolderId).OnDelete(DeleteBehavior.Cascade);
            message.Property(m => m.OwnerUserId).HasMaxLength(64);
            message.Property(m => m.Subject).HasMaxLength(2000);
            message.Property(m => m.From).HasConversion(Addresses, AddressComparer);
            message.Property(m => m.To).HasConversion(Addresses, AddressComparer);
            message.Property(m => m.Date).HasConversion(UtcTicks);
            message.Property(m => m.Flags).HasConversion<int>();
            message.Property(m => m.SearchText).HasMaxLength(4000);
            message.Property(m => m.Preview).HasMaxLength(MessagePreview.MaxLength);
            message.Property(m => m.BimiDomain).HasMaxLength(253);
            message.Property(m => m.BimiSelector).HasMaxLength(63);
            message.HasQueryFilter(m => m.OwnerUserId == _userId);
        });

        model.Entity<RemovedMessage>(removed =>
        {
            removed.ToTable("removed_messages");
            removed.HasKey(r => r.Id);
            removed.HasIndex(r => new { r.FolderId, r.ChangeSeq });
            removed.HasIndex(r => r.RemovedAt);
            removed.HasOne<CachedFolder>().WithMany().HasForeignKey(r => r.FolderId).OnDelete(DeleteBehavior.Cascade);
            removed.Property(r => r.OwnerUserId).HasMaxLength(64);
            removed.Property(r => r.RemovedAt).HasConversion(UtcTicks);
            removed.HasQueryFilter(r => r.OwnerUserId == _userId);
        });

        model.Entity<OAuthClient>(client =>
        {
            client.ToTable("oauth_clients");
            client.HasKey(c => c.ProviderId);
            client.Property(c => c.ProviderId).HasMaxLength(32);
            client.Property(c => c.ClientId).HasMaxLength(500);
            client.Property(c => c.Tenant).HasMaxLength(100);
            client.Property(c => c.RedirectUri).HasMaxLength(2000);
            client.Property(c => c.UpdatedAt).HasConversion(UtcTicks);
        });

        model.Entity<BimiLookup>(lookup =>
        {
            lookup.ToTable("bimi_lookups");
            lookup.HasKey(l => new { l.Domain, l.Selector });
            lookup.HasIndex(l => l.ExpiresAt);
            lookup.HasIndex(l => l.LogoHash);
            lookup.Property(l => l.Domain).HasMaxLength(253);
            lookup.Property(l => l.Selector).HasMaxLength(63);
            lookup.Property(l => l.LogoHash).HasMaxLength(64);
            lookup.Property(l => l.Outcome).HasConversion<string>().HasMaxLength(16);
            lookup.Property(l => l.CheckedAt).HasConversion(UtcTicks);
            lookup.Property(l => l.ExpiresAt).HasConversion(UtcTicks);
        });

        model.Entity<BimiLogo>(logo =>
        {
            logo.ToTable("bimi_logos");
            logo.HasKey(l => l.Hash);
            logo.Property(l => l.Hash).HasMaxLength(64);
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
