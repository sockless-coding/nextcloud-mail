using System.Collections.Concurrent;
using MailKit;
using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Contacts;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Senders;

/// <summary>A domain the user marked "Trust this domain": no suspicious-sender warning for it or its subdomains.</summary>
public sealed class TrustedSender
{
    public required string OwnerUserId { get; init; }

    /// <summary>The registered domain in lower-case ASCII (<see cref="SenderDomain.Registered"/>).</summary>
    public required string Domain { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Each user's <see cref="SenderIndex"/> and the verdicts reached with it, kept in memory for
/// <see cref="Fresh"/> (contacts and correspondents change slowly; trusting a domain rebuilds it at
/// once). Users who stop reading mail are forgotten after <see cref="Forget"/>.
/// </summary>
public sealed class SenderIndexCache(TimeProvider clock)
{
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(5);

    /// <summary>When the address books could not be read: try again sooner.</summary>
    public static readonly TimeSpan FreshWithoutContacts = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan Forget = TimeSpan.FromHours(1);
    public const int MaxVerdicts = 10_000;

    internal sealed class Entry
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public SenderIndex? Index { get; set; }
        public DateTimeOffset ValidUntil { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset UsedAt { get; set; }
        public ConcurrentDictionary<string, SenderWarning?> Verdicts { get; set; } = new();
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    internal async Task<Entry> GetAsync(string userId, Func<Task<(SenderIndex Index, bool Complete)>> build, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        foreach (var (key, stale) in _entries)
        {
            if (now - stale.UsedAt > Forget && key != userId)
            {
                _entries.TryRemove(key, out _);
            }
        }

        var entry = _entries.GetOrAdd(userId, _ => new Entry());
        entry.UsedAt = now;
        if (entry.Index is not null && now < entry.ValidUntil)
        {
            return entry;
        }

        await entry.Lock.WaitAsync(ct);
        try
        {
            if (entry.Index is null || clock.GetUtcNow() >= entry.ValidUntil)
            {
                var (index, complete) = await build();
                entry.Index = index;
                entry.Verdicts = new ConcurrentDictionary<string, SenderWarning?>();
                entry.ValidUntil = clock.GetUtcNow() + (complete ? Fresh : FreshWithoutContacts);
            }

            return entry;
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    public void Invalidate(string userId)
    {
        if (_entries.TryGetValue(userId, out var entry))
        {
            entry.ValidUntil = DateTimeOffset.MinValue;
        }
    }
}

/// <summary>
/// Suspicious-sender warnings (<see cref="SenderChecker"/>) with what the user knows: their accounts,
/// address books, the people they wrote to or answered (as far as the cache holds them) and the
/// domains they trust, on top of the bundled brand list; and how old the sender's domain is
/// (<see cref="DomainAges"/>), for domains that are none of these.
/// </summary>
public sealed class SenderChecks(
    MailDbContext db, SenderIndexCache cache, ContactDirectory directory, NextcloudContacts dav, FolderRoles roles,
    DomainAges domainAges, TimeProvider clock, ILogger<SenderChecks> logger)
{
    public const int MaxSenders = 200;

    /// <summary>Newest messages read per source of correspondents.</summary>
    public const int MaxCorrespondentMessages = 2000;

    public sealed record Sender(string? Name, string? Address);

    /// <summary>
    /// One verdict per sender, in order (null for those that look fine), and which of them may change shortly:
    /// <see cref="Pending"/> lists the senders whose domain age is being looked up; ask again in a few seconds.
    /// </summary>
    public sealed record Result(IReadOnlyList<SenderWarning?> Warnings, IReadOnlyList<int> Pending);

    public async Task<Result> CheckAsync(string userId, IReadOnlyList<Sender> senders, CancellationToken ct)
    {
        var entry = await cache.GetAsync(userId, () => BuildAsync(userId, ct), ct);
        var index = entry.Index!;
        if (entry.Verdicts.Count > SenderIndexCache.MaxVerdicts)
        {
            entry.Verdicts.Clear();
        }

        var warnings = new SenderWarning?[senders.Count];
        var undecided = new List<(int Position, string Key, string Address, string? AgeDomain)>();
        for (var i = 0; i < senders.Count; i++)
        {
            if (senders[i].Address is not { Length: > 0 and <= 320 } address)
            {
                continue;
            }

            var key = $"{senders[i].Name}\n{address.ToLowerInvariant()}";
            if (entry.Verdicts.TryGetValue(key, out var verdict))
            {
                warnings[i] = verdict;
            }
            else
            {
                undecided.Add((i, key, address, AgeDomain(index, address)));
            }
        }

        var ages = await domainAges.FindAsync(undecided.Select(u => u.AgeDomain).OfType<string>().ToHashSet(), ct);
        var pending = new List<int>();
        foreach (var (position, key, address, ageDomain) in undecided)
        {
            DomainAge? age = null;
            var waiting = ageDomain is not null && ages is not null && !ages.TryGetValue(ageDomain, out age);
            var verdict = SenderChecker.Check(index, senders[position].Name, address, domainAges.DaysOld(age));
            warnings[position] = verdict;
            if (waiting)
            {
                pending.Add(position);
            }
            else
            {
                entry.Verdicts[key] = verdict;
            }
        }

        return new Result(warnings, pending);
    }

    /// <summary>The open message's verdict, Reply-To and DMARC result included (not remembered).</summary>
    public async Task<SenderWarning?> CheckMessageAsync(string userId, AddressDto[] from, AddressDto[] replyTo, bool dmarcFailed, CancellationToken ct)
    {
        if (from is not [var author, ..])
        {
            return null;
        }

        var entry = await cache.GetAsync(userId, () => BuildAsync(userId, ct), ct);
        var index = entry.Index!;
        // Not waited for: the list usually asked moments before the message was opened.
        DomainAge? age = null;
        if (AgeDomain(index, author.Address) is { } domain)
        {
            (await domainAges.FindAsync([domain], ct))?.TryGetValue(domain, out age);
        }

        return SenderChecker.Check(index, author.Name, author.Address, replyTo.FirstOrDefault()?.Address, dmarcFailed, domainAges.DaysOld(age));
    }

    /// <summary>The registered domain whose age matters: not for known or trusted senders (nor asked about them).</summary>
    private static string? AgeDomain(SenderIndex index, string address) =>
        SenderDomain.OfAddress(address) is { } domain && !index.IsKnown(domain) && !index.IsTrusted(domain) ? domain.Registered : null;

    public async Task<IReadOnlyList<string>> ListTrustedAsync(CancellationToken ct) =>
        await db.TrustedSenders.OrderBy(t => t.Domain).Select(t => t.Domain).ToListAsync(ct);

    /// <returns>The registered domain now trusted; null when <paramref name="domain"/> is not a domain name.</returns>
    public async Task<string?> TrustAsync(string userId, string domain, CancellationToken ct)
    {
        if (SenderDomain.Parse(domain) is not { } parsed)
        {
            return null;
        }

        if (!await db.TrustedSenders.AnyAsync(t => t.Domain == parsed.Registered, ct))
        {
            db.TrustedSenders.Add(new TrustedSender { OwnerUserId = userId, Domain = parsed.Registered, CreatedAt = clock.GetUtcNow() });
            await db.SaveChangesAsync(ct);
        }

        cache.Invalidate(userId);
        return parsed.Registered;
    }

    public async Task<bool> DistrustAsync(string userId, string domain, CancellationToken ct)
    {
        var name = SenderDomain.Parse(domain)?.Registered ?? domain.ToLowerInvariant();
        var removed = await db.TrustedSenders.Where(t => t.Domain == name).ExecuteDeleteAsync(ct) > 0;
        cache.Invalidate(userId);
        return removed;
    }

    private async Task<(SenderIndex, bool)> BuildAsync(string userId, CancellationToken ct)
    {
        var domains = new List<KnownDomain>(SenderIndex.Brands);
        void Add(string? address, KnownSource source)
        {
            if (address is not null && SenderDomain.OfAddress(address) is { } domain)
            {
                domains.Add(KnownDomain.Of(domain, source));
            }
        }

        var accounts = await db.Accounts.AsNoTracking().ToListAsync(ct);
        foreach (var account in accounts)
        {
            Add(account.EmailAddress, KnownSource.Account);
        }

        var complete = true;
        try
        {
            foreach (var (_, contacts) in await directory.GetAsync(userId, dav, ct))
            {
                foreach (var email in contacts.SelectMany(c => c.Card.Emails))
                {
                    Add(email, KnownSource.Contact);
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Nextcloud unreachable, or no Contacts app: check against the rest.
            logger.LogInformation("Address books unavailable for the sender checks: {ExceptionType}", e.GetType().Name);
            complete = false;
        }

        // The Sent folders as the folder pane last listed them (the cache holds what was opened).
        var sentNames = accounts
            .Select(a => (a.Id, Name: roles.Find(a, anyAge: true)?.GetValueOrDefault("sent") is { } id ? MailboxIds.FolderFullName(id) : null))
            .Where(s => s.Name is not null)
            .ToList();
        var sentIds = new List<Guid>();
        foreach (var (accountId, name) in sentNames)
        {
            sentIds.AddRange(await db.Folders.Where(f => f.AccountId == accountId && f.FullName == name).Select(f => f.Id).ToListAsync(ct));
        }

        var recipients = await db.Messages.Where(m => sentIds.Contains(m.FolderId))
            .OrderByDescending(m => m.Date).Take(MaxCorrespondentMessages).Select(m => m.To).ToListAsync(ct);
        var answered = await db.Messages.Where(m => (m.Flags & MessageFlags.Answered) != 0)
            .OrderByDescending(m => m.Date).Take(MaxCorrespondentMessages).Select(m => m.From).ToListAsync(ct);
        foreach (var address in recipients.Concat(answered).SelectMany(list => list))
        {
            Add(address.Address, KnownSource.Correspondent);
        }

        var trusted = await db.TrustedSenders.Select(t => t.Domain).ToListAsync(ct);
        return (new SenderIndex(domains, trusted), complete);
    }
}
