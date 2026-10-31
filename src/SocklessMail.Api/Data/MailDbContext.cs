using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Bimi;
using SocklessMail.Api.Mailbox;
using SocklessMail.Api.OAuth;
using SocklessMail.Api.Rules;
using SocklessMail.Api.Scheduling;
using SocklessMail.Api.Senders;
using SocklessMail.Api.Sync;
using SocklessMail.Api.Tags;

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

    public DbSet<TrustedSender> TrustedSenders => Set<TrustedSender>();

    /// <summary>When sender domains were registered (RDAP): public registry data shared by all users, so not filtered.</summary>
    public DbSet<DomainAge> DomainAges => Set<DomainAge>();

    /// <summary>Instance-wide admin settings; not filtered.</summary>
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    public DbSet<ViewSettings> ViewSettings => Set<ViewSettings>();

    public DbSet<AccountViewState> AccountViewStates => Set<AccountViewState>();

    public DbSet<MailRule> Rules => Set<MailRule>();

    public DbSet<RuleCursor> RuleCursors => Set<RuleCursor>();

    public DbSet<MailTag> Tags => Set<MailTag>();

    public DbSet<TaggedMessage> TaggedMessages => Set<TaggedMessage>();

    public DbSet<TagScanState> TagScanStates => Set<TagScanState>();

    public DbSet<ScheduledMessage> ScheduledMessages => Set<ScheduledMessage>();

    public DbSet<SendLaterPresetList> SendLaterPresets => Set<SendLaterPresetList>();

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
            account.Property(a => a.TagStorage).HasConversion<string>().HasMaxLength(16).HasDefaultValue(TagStorage.Unknown).HasSentinel(TagStorage.Unknown);
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
            message.Property(m => m.Keywords).HasMaxLength(Keywords.MaxStoredLength);
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

        model.Entity<TrustedSender>(trusted =>
        {
            trusted.ToTable("trusted_senders");
            trusted.HasKey(t => new { t.OwnerUserId, t.Domain });
            trusted.Property(t => t.OwnerUserId).HasMaxLength(64);
            trusted.Property(t => t.Domain).HasMaxLength(253);
            trusted.Property(t => t.CreatedAt).HasConversion(UtcTicks);
            trusted.HasQueryFilter(t => t.OwnerUserId == _userId);
        });

        model.Entity<DomainAge>(age =>
        {
            age.ToTable("domain_ages");
            age.HasKey(a => a.Domain);
            age.HasIndex(a => a.ExpiresAt);
            age.Property(a => a.Domain).HasMaxLength(253);
            age.Property(a => a.Outcome).HasConversion<string>().HasMaxLength(16);
            age.Property(a => a.RegisteredAt).HasConversion(UtcTicks);
            age.Property(a => a.CheckedAt).HasConversion(UtcTicks);
            age.Property(a => a.ExpiresAt).HasConversion(UtcTicks);
        });

        model.Entity<AppSetting>(setting =>
        {
            setting.ToTable("app_settings");
            setting.HasKey(s => s.Key);
            setting.Property(s => s.Key).HasMaxLength(64);
            setting.Property(s => s.Value).HasMaxLength(4000);
            setting.Property(s => s.UpdatedAt).HasConversion(UtcTicks);
        });

        model.Entity<ViewSettings>(settings =>
        {
            settings.ToTable("view_settings");
            settings.HasKey(s => s.OwnerUserId);
            settings.Property(s => s.OwnerUserId).HasMaxLength(64);
            settings.Property(s => s.Storage).HasMaxLength(16);
            settings.Property(s => s.LastView).HasMaxLength(5000);
            settings.Property(s => s.UpdatedAt).HasConversion(UtcTicks);
            settings.HasQueryFilter(s => s.OwnerUserId == _userId);
        });

        model.Entity<AccountViewState>(state =>
        {
            state.ToTable("account_view_states");
            state.HasKey(s => s.AccountId);
            state.HasIndex(s => s.OwnerUserId);
            state.HasOne<MailAccount>().WithMany().HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Cascade);
            state.Property(s => s.OwnerUserId).HasMaxLength(64);
            state.Property(s => s.LastFolderId).HasMaxLength(4000);
            state.Property(s => s.UpdatedAt).HasConversion(UtcTicks);
            state.HasQueryFilter(s => s.OwnerUserId == _userId);
        });

        model.Entity<MailRule>(rule =>
        {
            rule.ToTable("rules");
            rule.HasKey(r => r.Id);
            rule.HasIndex(r => new { r.AccountId, r.SortOrder });
            rule.HasOne<MailAccount>().WithMany().HasForeignKey(r => r.AccountId).OnDelete(DeleteBehavior.Cascade);
            rule.Property(r => r.OwnerUserId).HasMaxLength(64);
            rule.Property(r => r.Name).HasMaxLength(RuleStore.MaxNameLength);
            rule.Property(r => r.Conditions).HasMaxLength(20000);
            rule.Property(r => r.Actions).HasMaxLength(50000);
            rule.Property(r => r.LastError).HasMaxLength(2000);
            rule.Property(r => r.LastErrorAt).HasConversion(UtcTicks);
            rule.Property(r => r.LastMatchedAt).HasConversion(UtcTicks);
            rule.Property(r => r.UpdatedAt).HasConversion(UtcTicks);
            rule.HasQueryFilter(r => r.OwnerUserId == _userId);
        });

        model.Entity<RuleCursor>(cursor =>
        {
            cursor.ToTable("rule_cursors");
            cursor.HasKey(c => c.AccountId);
            cursor.HasOne<MailAccount>().WithMany().HasForeignKey(c => c.AccountId).OnDelete(DeleteBehavior.Cascade);
            cursor.Property(c => c.OwnerUserId).HasMaxLength(64);
            cursor.Property(c => c.UpdatedAt).HasConversion(UtcTicks);
            cursor.HasQueryFilter(c => c.OwnerUserId == _userId);
        });

        model.Entity<MailTag>(tag =>
        {
            tag.ToTable("tags");
            tag.HasKey(t => t.Id);
            tag.HasIndex(t => new { t.OwnerUserId, t.Keyword }).IsUnique();
            tag.Property(t => t.OwnerUserId).HasMaxLength(64);
            tag.Property(t => t.Name).HasMaxLength(TagStore.MaxNameLength);
            tag.Property(t => t.Color).HasMaxLength(7);
            tag.Property(t => t.Keyword).HasMaxLength(Keywords.MaxKeywordLength);
            tag.Property(t => t.CreatedAt).HasConversion(UtcTicks);
            tag.HasQueryFilter(t => t.OwnerUserId == _userId);
        });

        model.Entity<TaggedMessage>(tagged =>
        {
            tagged.ToTable("tagged_messages");
            tagged.HasKey(m => m.Id);
            tagged.HasIndex(m => new { m.AccountId, m.FolderName, m.UidValidity, m.Uid });
            tagged.HasIndex(m => new { m.AccountId, m.MessageIdHeader });
            tagged.HasIndex(m => new { m.OwnerUserId, m.Date });
            // Deleting an account drops its index with it.
            tagged.HasOne<MailAccount>().WithMany().HasForeignKey(m => m.AccountId).OnDelete(DeleteBehavior.Cascade);
            tagged.Property(m => m.OwnerUserId).HasMaxLength(64);
            tagged.Property(m => m.FolderName).HasMaxLength(1000);
            tagged.Property(m => m.MessageIdHeader).HasMaxLength(1000);
            tagged.Property(m => m.Keywords).HasMaxLength(Keywords.MaxStoredLength);
            tagged.Property(m => m.Subject).HasMaxLength(2000);
            tagged.Property(m => m.From).HasConversion(Addresses, AddressComparer);
            tagged.Property(m => m.To).HasConversion(Addresses, AddressComparer);
            tagged.Property(m => m.Date).HasConversion(UtcTicks);
            tagged.Property(m => m.Preview).HasMaxLength(MessagePreview.MaxLength);
            tagged.Property(m => m.BimiDomain).HasMaxLength(253);
            tagged.Property(m => m.BimiSelector).HasMaxLength(63);
            tagged.Property(m => m.UpdatedAt).HasConversion(UtcTicks);
            tagged.HasQueryFilter(m => m.OwnerUserId == _userId);
        });

        model.Entity<TagScanState>(scan =>
        {
            scan.ToTable("tag_scan_states");
            scan.HasKey(s => new { s.AccountId, s.FolderName });
            scan.HasOne<MailAccount>().WithMany().HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Cascade);
            scan.Property(s => s.OwnerUserId).HasMaxLength(64);
            scan.Property(s => s.FolderName).HasMaxLength(1000);
            scan.Property(s => s.HighestModSeq).HasConversion<long>();
            scan.Property(s => s.KeywordSet).HasMaxLength(20000);
            scan.Property(s => s.ScannedAt).HasConversion(UtcTicks);
            scan.HasQueryFilter(s => s.OwnerUserId == _userId);
        });

        model.Entity<ScheduledMessage>(scheduled =>
        {
            scheduled.ToTable("scheduled_messages");
            scheduled.HasKey(m => m.Id);
            scheduled.HasIndex(m => new { m.Status, m.DueAt });
            scheduled.HasIndex(m => new { m.OwnerUserId, m.SendAt });
            // Deleting an account drops what it had scheduled (the files go with the next sweep).
            scheduled.HasOne<MailAccount>().WithMany().HasForeignKey(m => m.AccountId).OnDelete(DeleteBehavior.Cascade);
            scheduled.Property(m => m.OwnerUserId).HasMaxLength(64);
            scheduled.Property(m => m.Status).HasConversion<string>().HasMaxLength(16);
            scheduled.Property(m => m.SendAt).HasConversion(UtcTicks);
            scheduled.Property(m => m.DueAt).HasConversion(UtcTicks);
            scheduled.Property(m => m.ErrorCode).HasMaxLength(64);
            scheduled.Property(m => m.Error).HasMaxLength(2000);
            scheduled.Property(m => m.Subject).HasMaxLength(2000);
            scheduled.Property(m => m.Recipients).HasConversion(Addresses, AddressComparer);
            scheduled.Property(m => m.Preview).HasMaxLength(MessagePreview.MaxLength);
            scheduled.Property(m => m.MessageIdHeader).HasMaxLength(1000);
            scheduled.Property(m => m.SourceFolderId).HasMaxLength(4000);
            scheduled.Property(m => m.SourceMessageId).HasMaxLength(100);
            scheduled.Property(m => m.SourceMode).HasConversion<string>().HasMaxLength(16);
            scheduled.Property(m => m.CreatedAt).HasConversion(UtcTicks);
            scheduled.Property(m => m.UpdatedAt).HasConversion(UtcTicks);
            scheduled.Ignore(m => m.Source);
            scheduled.HasQueryFilter(m => m.OwnerUserId == _userId);
        });

        model.Entity<SendLaterPresetList>(presets =>
        {
            presets.ToTable("send_later_presets");
            presets.HasKey(p => p.OwnerUserId);
            presets.Property(p => p.OwnerUserId).HasMaxLength(64);
            presets.Property(p => p.Presets).HasMaxLength(20000);
            presets.Property(p => p.UpdatedAt).HasConversion(UtcTicks);
            presets.HasQueryFilter(p => p.OwnerUserId == _userId);
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
