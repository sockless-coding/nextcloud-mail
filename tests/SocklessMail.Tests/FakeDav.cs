using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using SocklessMail.Api.AppApi;

namespace SocklessMail.Tests;

/// <summary>
/// Just enough of Nextcloud's WebDAV for the Files features: GET, PUT (If-None-Match), PROPFIND
/// depth 1 and SEARCH by file id. Like Nextcloud, a user only reaches their own files.
/// Plug it in with <c>app.Nextcloud.Respond = dav.Respond</c>.
/// </summary>
public sealed partial class FakeDav
{
    private const string Prefix = "/remote.php/dav/files/";

    private sealed record Item(string Content, string ContentType, long FileId, bool IsFolder);

    private readonly ConcurrentDictionary<string, Item> _items = new();
    private long _nextId = 100;

    /// <summary>Paths that answer PUT with 412 once, as if someone created the file just before us.</summary>
    public ConcurrentDictionary<string, bool> TakenOnPut { get; } = new();

    /// <summary>Overrides the PUT status (e.g. 507 for a full quota).</summary>
    public HttpStatusCode? PutStatus { get; set; }

    public long AddFolder(string user, string path) => Add(user, path, new Item("", "httpd/unix-directory", Interlocked.Increment(ref _nextId), true));

    public long AddFile(string user, string path, string content, string contentType = "text/plain") =>
        Add(user, path, new Item(content, contentType, Interlocked.Increment(ref _nextId), false));

    public string? Read(string user, string path) => _items.TryGetValue(Key(user, path), out var item) ? item.Content : null;

    private long Add(string user, string path, Item item)
    {
        _items[Key(user, path)] = item;
        return item.FileId;
    }

    private static string Key(string user, string path) => $"{user}/{path.Trim('/')}";

    public HttpResponseMessage? Respond(RecordedRequest request)
    {
        var path = request.PathAndQuery.Split('?')[0];
        if (!path.StartsWith("/remote.php/dav/", StringComparison.Ordinal))
        {
            return null;
        }

        var authUser = AuthorizedUser(request);
        if (request.Method.Method == "SEARCH")
        {
            return Search(authUser!, request.Body);
        }

        var names = path[Prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        var user = names[0];
        if (user != authUser)
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        var key = $"{user}/{string.Join('/', names[1..])}";
        _items.TryGetValue(key, out var item);
        var isRoot = names.Length == 1;
        switch (request.Method.Method)
        {
            case "GET":
                if (item is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                // As Nextcloud does: a page about WebDAV, not an error.
                return item.IsFolder
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>WebDAV</html>", Encoding.UTF8, "text/html") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(item.Content, Encoding.UTF8, item.ContentType) };

            case "PUT":
                if (PutStatus is { } status)
                {
                    return new HttpResponseMessage(status);
                }

                var parent = $"{user}/{string.Join('/', names[1..^1])}";
                if (names.Length > 2 && !_items.ContainsKey(parent))
                {
                    return new HttpResponseMessage(HttpStatusCode.Conflict);
                }

                if (TakenOnPut.TryRemove(key, out _))
                {
                    _items[key] = new Item("someone else's", "text/plain", Interlocked.Increment(ref _nextId), false);
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                }

                if (item is not null && request.Headers.TryGetValue("If-None-Match", out var match) && match == "*")
                {
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                }

                _items[key] = new Item(request.Body, "application/octet-stream", Interlocked.Increment(ref _nextId), false);
                return new HttpResponseMessage(HttpStatusCode.Created);

            case "PROPFIND":
                if (!isRoot && item is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                var prefix = key.TrimEnd('/') + "/";
                var depthZero = request.Headers.GetValueOrDefault("Depth") == "0";
                var children = _items.Where(e => !depthZero && e.Key.StartsWith(prefix, StringComparison.Ordinal) && !e.Key[prefix.Length..].Contains('/'));
                var responses = new List<string> { Response(user, key, item ?? new Item("", "", 1, true)) };
                responses.AddRange(children.Select(c => Response(user, c.Key, c.Value)));
                return MultiStatus(responses);

            default:
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }
    }

    private HttpResponseMessage Search(string user, string body)
    {
        var ids = LiteralPattern().Matches(body).Select(m => long.Parse(m.Groups[1].Value)).ToHashSet();
        var hits = _items.Where(e => e.Key.StartsWith(user + "/", StringComparison.Ordinal) && ids.Contains(e.Value.FileId));
        return MultiStatus(hits.Select(h => Response(user, h.Key, h.Value)));
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

    private static string Response(string user, string key, Item item)
    {
        var relative = key[(user.Length + 1)..];
        var href = "/nextcloud" + Prefix + Uri.EscapeDataString(user) + "/"
            + string.Join('/', relative.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString))
            + (item.IsFolder && relative.Length > 0 ? "/" : "");
        var type = item.IsFolder ? "<d:collection/>" : "";
        var length = item.IsFolder ? "" : $"<d:getcontentlength>{Encoding.UTF8.GetByteCount(item.Content)}</d:getcontentlength>";
        return $"<d:response><d:href>{href}</d:href><d:propstat><d:prop><oc:fileid>{item.FileId}</oc:fileid><d:resourcetype>{type}</d:resourcetype>{length}</d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";
    }

    private static HttpResponseMessage MultiStatus(IEnumerable<string> responses) =>
        new((HttpStatusCode)207)
        {
            Content = new StringContent(
                $"""<?xml version="1.0"?><d:multistatus xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns">{string.Concat(responses)}</d:multistatus>""",
                Encoding.UTF8, "application/xml"),
        };

    [GeneratedRegex(@"<d:literal>(\d+)</d:literal>")]
    private static partial Regex LiteralPattern();
}
