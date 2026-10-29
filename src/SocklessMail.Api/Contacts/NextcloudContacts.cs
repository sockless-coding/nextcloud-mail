using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using SocklessMail.Api.AppApi;

namespace SocklessMail.Api.Contacts;

/// <summary>An address book of the user, by its name in their address book home ("contacts").</summary>
public sealed record AddressBook(string Id, string Name, string? Ctag, bool Writable);

/// <summary>A contact with at least one email address. Id is "&lt;book&gt;/&lt;card&gt;".</summary>
public sealed record Contact(string Id, VCard Card);

/// <summary>What a contacts operation could not do, in words the user can act on.</summary>
public sealed class NextcloudContactsException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;

    public int StatusCode { get; } = statusCode;

    public static NextcloudContactsException NotFound() =>
        new("not_found", "The contact or address book no longer exists.", StatusCodes.Status404NotFound);

    public static NextcloudContactsException Unavailable() =>
        new("nextcloud_unavailable", "Nextcloud Contacts did not answer. Try again in a moment.", StatusCodes.Status502BadGateway);
}

/// <summary>
/// The user's Nextcloud address books over CardDAV (remote.php/dav/addressbooks/users/&lt;user&gt;),
/// as that user through AppAPI. Their home lists their own books, books shared with them, the
/// system address book ("Accounts") and "Recently contacted"; Nextcloud decides which are writable.
/// </summary>
public sealed class NextcloudContacts(NextcloudClient nextcloud, ILogger<NextcloudContacts> logger)
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace Card = "urn:ietf:params:xml:ns:carddav";
    private static readonly XNamespace CalendarServer = "http://calendarserver.org/ns/";

    public async Task<IReadOnlyList<AddressBook>> ListAddressBooksAsync(string userId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), HomeUrl(userId))
        {
            Content = Xml("""<d:propfind xmlns:d="DAV:" xmlns:cs="http://calendarserver.org/ns/"><d:prop><d:resourcetype/><d:displayname/><cs:getctag/><d:current-user-privilege-set/></d:prop></d:propfind>"""),
        };
        request.Headers.Add("Depth", "1");
        var books = new List<AddressBook>();
        foreach (var (href, props) in await MultistatusAsync(request, userId, ct))
        {
            if (props.Element(Dav + "resourcetype")?.Element(Card + "addressbook") is null || LastSegment(href) is not { } id)
            {
                continue;
            }

            var privileges = props.Element(Dav + "current-user-privilege-set")?.Elements(Dav + "privilege")
                .SelectMany(p => p.Elements()).Select(e => e.Name.LocalName).ToHashSet() ?? [];
            var name = props.Element(Dav + "displayname")?.Value;
            books.Add(new AddressBook(id, string.IsNullOrWhiteSpace(name) ? id : name,
                props.Element(CalendarServer + "getctag")?.Value,
                privileges.Contains("all") || privileges.Contains("bind") || privileges.Contains("write-content")));
        }

        return books;
    }

    /// <summary>The contacts of a book that have an email address; only the properties mail needs are fetched (no photos).</summary>
    public async Task<IReadOnlyList<Contact>> ListContactsAsync(string userId, string bookId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod("REPORT"), BookUrl(userId, bookId))
        {
            Content = Xml("""<card:addressbook-query xmlns:d="DAV:" xmlns:card="urn:ietf:params:xml:ns:carddav"><d:prop><card:address-data><card:prop name="UID"/><card:prop name="FN"/><card:prop name="N"/><card:prop name="EMAIL"/><card:prop name="CATEGORIES"/><card:prop name="ORG"/></card:address-data></d:prop><card:filter><card:prop-filter name="EMAIL"/></card:filter></card:addressbook-query>"""),
        };
        request.Headers.Add("Depth", "1");
        var contacts = new List<Contact>();
        foreach (var (href, props) in await MultistatusAsync(request, userId, ct))
        {
            if (props.Element(Card + "address-data")?.Value is { Length: > 0 } data && LastSegment(href) is { } card)
            {
                var vcard = VCard.Parse(data);
                if (vcard.Emails.Count > 0)
                {
                    contacts.Add(new Contact($"{bookId}/{card}", vcard));
                }
            }
        }

        return contacts;
    }

    /// <summary>Creates a contact in a writable book; returns its id.</summary>
    public async Task<string> CreateAsync(string userId, string bookId, string name, string email, CancellationToken ct)
    {
        var uid = Guid.NewGuid().ToString();
        var cardName = uid + ".vcf";
        using var request = new HttpRequestMessage(HttpMethod.Put, CardUrl(userId, bookId, cardName))
        {
            Content = new StringContent(VCard.Create(uid, name, email), Encoding.UTF8, "text/vcard"),
        };
        request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        using var response = await SendAsync(request, userId, ct);
        ThrowForWrite(response, "PUT");
        return $"{bookId}/{cardName}";
    }

    /// <summary>Adds an address to an existing contact, keeping everything else in the card as it was.</summary>
    public async Task AddEmailAsync(string userId, string contactId, string email, CancellationToken ct)
    {
        var (bookId, cardName) = SplitId(contactId);
        string text;
        EntityTagHeaderValue? etag;
        using (var get = new HttpRequestMessage(HttpMethod.Get, CardUrl(userId, bookId, cardName)))
        using (var response = await SendAsync(get, userId, ct))
        {
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
            {
                throw NextcloudContactsException.NotFound();
            }

            EnsureSuccess(response, "GET");
            text = await response.Content.ReadAsStringAsync(ct);
            etag = response.Headers.ETag;
        }

        if (VCard.Parse(text).Emails.Contains(email, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        using var put = new HttpRequestMessage(HttpMethod.Put, CardUrl(userId, bookId, cardName))
        {
            Content = new StringContent(VCard.AddEmail(text, email), Encoding.UTF8, "text/vcard"),
        };
        if (etag is not null)
        {
            // Changed by someone else since we read it: theirs wins, the user tries again.
            put.Headers.IfMatch.Add(etag);
        }

        using var result = await SendAsync(put, userId, ct);
        if (result.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new NextcloudContactsException("changed", "The contact was changed in the meantime. Try again.", StatusCodes.Status409Conflict);
        }

        ThrowForWrite(result, "PUT");
    }

    /// <summary>"contacts/abc.vcf" → ("contacts", "abc.vcf"); anything else is not a contact id.</summary>
    public static (string Book, string Card) SplitId(string? contactId)
    {
        var parts = (contactId ?? "").Split('/');
        return parts.Length == 2 && parts.All(ValidSegment) ? (parts[0], parts[1]) : throw NextcloudContactsException.NotFound();
    }

    public static bool ValidSegment(string? segment) =>
        !string.IsNullOrEmpty(segment) && segment.Length <= 255 && segment is not ("." or "..")
        && !segment.Any(c => char.IsControl(c) || c is '/' or '\\');

    private void ThrowForWrite(HttpResponseMessage response, string method)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed:
                throw new NextcloudContactsException("read_only", "This address book is read-only.", StatusCodes.Status403Forbidden);
            case HttpStatusCode.NotFound or HttpStatusCode.Conflict:
                throw NextcloudContactsException.NotFound();
            case HttpStatusCode.PreconditionFailed:
                throw new NextcloudContactsException("changed", "The contact was changed in the meantime. Try again.", StatusCodes.Status409Conflict);
            default:
                EnsureSuccess(response, method);
                break;
        }
    }

    private async Task<List<(string Href, XElement Props)>> MultistatusAsync(HttpRequestMessage request, string userId, CancellationToken ct)
    {
        using var response = await SendAsync(request, userId, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            throw NextcloudContactsException.NotFound();
        }

        EnsureSuccess(response, request.Method.Method);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, ct);
        var result = new List<(string, XElement)>();
        foreach (var item in document.Root?.Elements(Dav + "response") ?? [])
        {
            var props = item.Elements(Dav + "propstat")
                .Where(p => p.Element(Dav + "status")?.Value.Contains(" 200 ") == true)
                .Select(p => p.Element(Dav + "prop"))
                .FirstOrDefault();
            if (item.Element(Dav + "href")?.Value is { } href && props is not null)
            {
                result.Add((href, props));
            }
        }

        return result;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string userId, CancellationToken ct)
    {
        try
        {
            return await nextcloud.DavAsync(request, userId, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("CardDAV {Method} failed: {Error}", request.Method, e.Message);
            throw NextcloudContactsException.Unavailable();
        }
    }

    private void EnsureSuccess(HttpResponseMessage response, string method)
    {
        if (!response.IsSuccessStatusCode)
        {
            // Status only: error bodies can echo contact data.
            logger.LogWarning("CardDAV {Method} returned HTTP {Status}", method, (int)response.StatusCode);
            throw NextcloudContactsException.Unavailable();
        }
    }

    private static StringContent Xml(string body) => new("""<?xml version="1.0"?>""" + body, Encoding.UTF8, "text/xml");

    /// <summary>The decoded last path segment of an href ("…/contacts/" → "contacts").</summary>
    private static string? LastSegment(string href)
    {
        var path = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(href, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : href;
        var segment = path.TrimEnd('/').Split('/')[^1];
        var decoded = Uri.UnescapeDataString(segment);
        return ValidSegment(decoded) ? decoded : null;
    }

    private static string HomeUrl(string userId) => $"remote.php/dav/addressbooks/users/{Uri.EscapeDataString(userId)}/";

    private static string BookUrl(string userId, string bookId) =>
        ValidSegment(bookId) ? $"{HomeUrl(userId)}{Uri.EscapeDataString(bookId)}/" : throw NextcloudContactsException.NotFound();

    private static string CardUrl(string userId, string bookId, string cardName) =>
        ValidSegment(cardName) ? BookUrl(userId, bookId) + Uri.EscapeDataString(cardName) : throw NextcloudContactsException.NotFound();
}
