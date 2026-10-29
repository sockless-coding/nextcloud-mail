using System.Collections.Concurrent;

namespace SocklessMail.Api.Contacts;

public sealed record ContactMatch(string Id, string Name, string Address, string AddressBook);

public sealed record GroupMember(string Name, string Address);

/// <summary>A contact group (the Contacts app's groups are vCard CATEGORIES) with each member's first address.</summary>
public sealed record GroupMatch(string Name, IReadOnlyList<GroupMember> Members);

public sealed record ContactSearchResult(IReadOnlyList<ContactMatch> Contacts, IReadOnlyList<GroupMatch> Groups);

/// <summary>
/// Each user's address books, kept in memory for searching as they type. A book is fetched again
/// only when its ctag changed; the list of books is checked at most every <see cref="Fresh"/>.
/// Users who stop searching are forgotten after <see cref="Forget"/>.
/// </summary>
public sealed class ContactDirectory(TimeProvider clock)
{
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Forget = TimeSpan.FromHours(1);
    public const int MaxContacts = 8;
    public const int MaxGroups = 3;
    public const int MaxGroupMembers = 100;

    /// <summary>Contacts the app collects by itself; searched, but never the source of groups.</summary>
    private const string RecentPrefix = "z-app-generated--";

    private sealed class Entry
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);
        public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset UsedAt { get; set; }
        public List<(AddressBook Book, IReadOnlyList<Contact> Contacts)> Books { get; set; } = [];
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>The user's books, best first: writable (their own) ones, then shared and system ones, then collected ones.</summary>
    public async Task<IReadOnlyList<(AddressBook Book, IReadOnlyList<Contact> Contacts)>> GetAsync(
        string userId, NextcloudContacts dav, CancellationToken ct)
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
        if (now - entry.CheckedAt < Fresh)
        {
            return entry.Books;
        }

        await entry.Lock.WaitAsync(ct);
        try
        {
            if (clock.GetUtcNow() - entry.CheckedAt < Fresh)
            {
                return entry.Books;
            }

            var books = new List<(AddressBook, IReadOnlyList<Contact>)>();
            foreach (var book in await dav.ListAddressBooksAsync(userId, ct))
            {
                var known = entry.Books.FirstOrDefault(b => b.Book.Id == book.Id);
                if (known.Book is not null && book.Ctag is not null && known.Book.Ctag == book.Ctag)
                {
                    books.Add((book, known.Contacts));
                    continue;
                }

                try
                {
                    books.Add((book, await dav.ListContactsAsync(userId, book.Id, ct)));
                }
                catch (NextcloudContactsException e) when (e.Code == "not_found")
                {
                    // Gone, or not readable after all (a share withdrawn while listing).
                }
            }

            entry.Books = [.. books.OrderBy(b => b.Item1.Id.StartsWith(RecentPrefix, StringComparison.Ordinal))
                .ThenBy(b => !b.Item1.Writable)];
            entry.CheckedAt = clock.GetUtcNow();
            return entry.Books;
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    /// <summary>After a change made through the app: the next search checks the books again.</summary>
    public void Invalidate(string userId)
    {
        if (_entries.TryGetValue(userId, out var entry))
        {
            entry.CheckedAt = DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// Addresses whose name, address or organization contains <paramref name="query"/> (words
    /// starting with it first), each address once; and groups whose name contains it.
    /// </summary>
    public static ContactSearchResult Search(IReadOnlyList<(AddressBook Book, IReadOnlyList<Contact> Contacts)> books, string query)
    {
        var q = query.Trim();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = new List<(int Rank, ContactMatch Match)>();
        foreach (var (book, contacts) in books)
        {
            foreach (var contact in contacts)
            {
                var card = contact.Card;
                var nameRank = Rank(card.Name, q);
                // An organization match ranks after name and address matches.
                var orgRank = card.Organization is not null && Rank(card.Organization, q) is var r && r != int.MaxValue ? r + 1 : int.MaxValue;
                foreach (var email in card.Emails)
                {
                    var rank = Math.Min(Math.Min(nameRank, Rank(email, q)), orgRank);
                    if (rank != int.MaxValue && seen.Add(email))
                    {
                        matches.Add((rank, new ContactMatch(contact.Id, card.Name, email, book.Name)));
                    }
                }
            }
        }

        var groups = books
            .Where(b => !b.Book.Id.StartsWith(RecentPrefix, StringComparison.Ordinal))
            .SelectMany(b => b.Contacts)
            .SelectMany(c => c.Card.Groups.Select(g => (Group: g, Contact: c)))
            .Where(x => x.Group.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .GroupBy(x => x.Group, StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(g => Rank(g.Key, q)).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxGroups)
            .Select(g => new GroupMatch(g.First().Group, g
                .Select(x => new GroupMember(x.Contact.Card.Name, x.Contact.Card.Emails[0]))
                .DistinctBy(m => m.Address, StringComparer.OrdinalIgnoreCase)
                .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(MaxGroupMembers)
                .ToList()))
            .ToList();

        return new ContactSearchResult(
            matches.OrderBy(m => m.Rank).ThenBy(m => m.Match.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(MaxContacts).Select(m => m.Match).ToList(),
            groups);
    }

    /// <summary>Contacts that have this address, in the books' order.</summary>
    public static IReadOnlyList<ContactMatch> ByAddress(IReadOnlyList<(AddressBook Book, IReadOnlyList<Contact> Contacts)> books, string address) =>
        books.SelectMany(b => b.Contacts
                .Where(c => c.Card.Emails.Contains(address, StringComparer.OrdinalIgnoreCase))
                .Select(c => new ContactMatch(c.Id, c.Card.Name, address, b.Book.Name)))
            .ToList();

    /// <summary>0: the text or one of its words starts with the query; 1: contains it; MaxValue: no match.</summary>
    private static int Rank(string text, string query)
    {
        var index = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (index < 0)
        {
            return int.MaxValue;
        }

        return index == 0 || text.Split([' ', '.', '-', '_', '@'], StringSplitOptions.RemoveEmptyEntries)
            .Any(w => w.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)) ? 0 : 1;
    }
}
