using System.Collections.Concurrent;
using System.Net;
using System.Security;
using System.Text;
using SocklessMail.Api.AppApi;

namespace SocklessMail.Tests;

/// <summary>
/// Just enough of Nextcloud's CardDAV: the address book home (PROPFIND), addressbook-query
/// REPORT (cards with EMAIL), GET and PUT with etags. A user only reaches their own home.
/// Plug it in with <c>app.Nextcloud.Respond = carddav.Respond</c>.
/// </summary>
public sealed class FakeCardDav
{
    private const string Prefix = "/remote.php/dav/addressbooks/users/";

    private sealed class Book
    {
        public required string Name { get; init; }
        public bool Writable { get; init; }
        public int Ctag { get; set; } = 1;
        public ConcurrentDictionary<string, (string Text, int Version)> Cards { get; } = new();
    }

    private readonly ConcurrentDictionary<string, Book> _books = new();

    /// <summary>REPORTs per book id, to see what was fetched again.</summary>
    public ConcurrentDictionary<string, int> Reports { get; } = new();

    public void AddBook(string user, string id, string name, bool writable = true) =>
        _books[$"{user}/{id}"] = new Book { Name = name, Writable = writable };

    public void AddCard(string user, string book, string card, string vcard)
    {
        var b = _books[$"{user}/{book}"];
        b.Cards[card] = (vcard.Replace("\n", "\r\n"), 1);
        b.Ctag++;
    }

    public string? Card(string user, string book, string card) =>
        _books.TryGetValue($"{user}/{book}", out var b) && b.Cards.TryGetValue(card, out var c) ? c.Text : null;

    public IEnumerable<string> Cards(string user, string book) => _books[$"{user}/{book}"].Cards.Keys;

    public HttpResponseMessage? Respond(RecordedRequest request)
    {
        var path = request.PathAndQuery.Split('?')[0];
        if (!path.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var names = path[Prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (names[0] != AuthorizedUser(request))
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        var user = names[0];
        if (names.Length == 1 && request.Method.Method == "PROPFIND")
        {
            var books = _books.Where(b => b.Key.StartsWith(user + "/", StringComparison.Ordinal)).Select(b =>
            {
                var privileges = b.Value.Writable ? "<d:privilege><d:read/></d:privilege><d:privilege><d:bind/></d:privilege><d:privilege><d:write-content/></d:privilege>" : "<d:privilege><d:read/></d:privilege>";
                return $"<d:response><d:href>{Prefix}{user}/{Uri.EscapeDataString(b.Key[(user.Length + 1)..])}/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/><card:addressbook/></d:resourcetype><d:displayname>{SecurityElement.Escape(b.Value.Name)}</d:displayname><cs:getctag>{b.Value.Ctag}</cs:getctag><d:current-user-privilege-set>{privileges}</d:current-user-privilege-set></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";
            });
            var home = $"<d:response><d:href>{Prefix}{user}/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";
            return MultiStatus(home + string.Concat(books));
        }

        if (!_books.TryGetValue($"{user}/{names[1]}", out var book))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (names.Length == 2 && request.Method.Method == "REPORT")
        {
            Reports.AddOrUpdate(names[1], 1, (_, n) => n + 1);
            var cards = book.Cards.Where(c => c.Value.Text.Contains("EMAIL", StringComparison.Ordinal)).Select(c =>
                $"<d:response><d:href>{Prefix}{user}/{names[1]}/{Uri.EscapeDataString(c.Key)}</d:href><d:propstat><d:prop><card:address-data>{SecurityElement.Escape(c.Value.Text)}</card:address-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>");
            return MultiStatus(string.Concat(cards));
        }

        var name = names[2];
        book.Cards.TryGetValue(name, out var existing);
        switch (request.Method.Method)
        {
            case "GET":
                if (existing.Text is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(existing.Text, Encoding.UTF8, "text/vcard") };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"v{existing.Version}\"");
                return response;
            case "PUT":
                if (!book.Writable)
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden);
                }

                if ((request.Headers.GetValueOrDefault("If-None-Match") == "*" && existing.Text is not null)
                    || (request.Headers.TryGetValue("If-Match", out var match) && match != $"\"v{existing.Version}\""))
                {
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                }

                book.Cards[name] = (request.Body, existing.Version + 1);
                book.Ctag++;
                return new HttpResponseMessage(existing.Text is null ? HttpStatusCode.Created : HttpStatusCode.NoContent);
            default:
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }
    }

    /// <summary>Simulates an edit in another client: the card's etag changes.</summary>
    public void Touch(string user, string book, string card)
    {
        var b = _books[$"{user}/{book}"];
        var (text, version) = b.Cards[card];
        b.Cards[card] = (text, version + 1);
        b.Ctag++;
    }

    private static string? AuthorizedUser(RecordedRequest request)
    {
        var header = request.Headers.GetValueOrDefault(AppApiHeaders.Authorization);
        if (header is null)
        {
            return null;
        }

        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header));
        return decoded[..decoded.IndexOf(':')];
    }

    private static HttpResponseMessage MultiStatus(string responses) =>
        new((HttpStatusCode)207)
        {
            Content = new StringContent(
                $"""<?xml version="1.0"?><d:multistatus xmlns:d="DAV:" xmlns:card="urn:ietf:params:xml:ns:carddav" xmlns:cs="http://calendarserver.org/ns/">{responses}</d:multistatus>""",
                Encoding.UTF8, "application/xml"),
        };
}
