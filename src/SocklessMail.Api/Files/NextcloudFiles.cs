using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using SocklessMail.Api.AppApi;

namespace SocklessMail.Api.Files;

/// <summary>A file or folder in a user's Nextcloud Files, by its path below the user's root ("/Documents/a.pdf").</summary>
public sealed record FilesEntry(string Path, string Name, bool IsFolder, long? Size);

/// <summary>What a Files operation could not do, in words the user can act on.</summary>
public sealed class NextcloudFilesException(string code, string message, int statusCode = StatusCodes.Status400BadRequest)
    : Exception(message)
{
    public string Code { get; } = code;

    public int StatusCode { get; } = statusCode;

    public static NextcloudFilesException NotFound() =>
        new("not_found", "The file no longer exists in Files.", StatusCodes.Status404NotFound);

    public static NextcloudFilesException FolderNotFound() =>
        new("folder_not_found", "The folder no longer exists in Files.", StatusCodes.Status404NotFound);

    public static NextcloudFilesException Unavailable() =>
        new("nextcloud_unavailable", "Nextcloud Files did not answer. Try again in a moment.", StatusCodes.Status502BadGateway);
}

/// <summary>
/// The user's Nextcloud Files over WebDAV (remote.php/dav/files/&lt;user&gt;), acting as that user
/// through AppAPI, plus public share links through the OCS sharing API. Nextcloud applies the
/// user's own permissions, quota and sharing policy to everything done here.
/// </summary>
public sealed class NextcloudFiles(NextcloudClient nextcloud, ILogger<NextcloudFiles> logger)
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace Oc = "http://owncloud.org/ns";

    /// <summary>Tries before giving up on a free name when other uploads keep taking it.</summary>
    private const int NameAttempts = 5;

    /// <summary>
    /// Splits a user-relative path into its names, or returns null when it is not one: empty,
    /// "." or ".." parts, backslashes or control characters. "/" alone is the root (no names).
    /// </summary>
    public static string[]? SplitPath(string? path)
    {
        if (path is null || path.Length > 4096 || path.Any(c => char.IsControl(c) || c == '\\'))
        {
            return null;
        }

        var names = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return names.Any(n => n is "." or ".." || n.Trim().Length == 0) ? null : names;
    }

    public static string JoinPath(IEnumerable<string> names) => "/" + string.Join('/', names);

    /// <summary>
    /// Reads a file. <paramref name="read"/> gets its name, content type, length (when Nextcloud
    /// tells it) and content, and runs while the response is open.
    /// </summary>
    public async Task<T> ReadAsync<T>(
        string userId, string path, Func<string, string, long?, Stream, Task<T>> read, CancellationToken ct)
    {
        var names = SplitPath(path) is { Length: > 0 } n ? n : throw NextcloudFilesException.NotFound();
        // Asked first: a GET on a folder answers 200 with Nextcloud's HTML page about WebDAV.
        var entry = await StatAsync(userId, names, ct) ?? throw NextcloudFilesException.NotFound();
        if (entry.IsFolder)
        {
            throw new NextcloudFilesException("is_folder", "Folders cannot be attached; insert a share link instead.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, FileUrl(userId, names));
        using var response = await SendAsync(request, userId, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            throw NextcloudFilesException.NotFound();
        }

        EnsureSuccess(response, "GET");
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        await using var content = await response.Content.ReadAsStreamAsync(ct);
        return await read(names[^1], contentType, response.Content.Headers.ContentLength ?? entry.Size, content);
    }

    /// <summary>The file or folder at a path, or null when there is none (or the user cannot see it).</summary>
    private async Task<FilesEntry?> StatAsync(string userId, string[] names, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), FileUrl(userId, names))
        {
            Content = new StringContent(
                """<?xml version="1.0"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getcontentlength/></d:prop></d:propfind>""",
                Encoding.UTF8, "text/xml"),
        };
        request.Headers.Add("Depth", "0");
        using var response = await SendAsync(request, userId, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return null;
        }

        EnsureSuccess(response, "PROPFIND");
        return (await ReadMultistatusAsync(response, ct)).Select(e => e.Entry).FirstOrDefault();
    }

    /// <summary>Looks files up by their Nextcloud file id (what the Files app passes on); unknown ids are left out.</summary>
    public async Task<IReadOnlyList<(long FileId, FilesEntry Entry)>> FindByIdsAsync(
        string userId, IReadOnlyCollection<long> fileIds, CancellationToken ct)
    {
        if (fileIds.Count == 0)
        {
            return [];
        }

        var conditions = fileIds.Select(id => new XElement(Dav + "eq",
            new XElement(Dav + "prop", new XElement(Oc + "fileid")),
            new XElement(Dav + "literal", id.ToString(CultureInfo.InvariantCulture)))).ToList();
        var where = conditions.Count == 1 ? conditions[0] : new XElement(Dav + "or", conditions);
        var search = new XDocument(new XElement(Dav + "searchrequest",
            new XAttribute(XNamespace.Xmlns + "d", Dav), new XAttribute(XNamespace.Xmlns + "oc", Oc),
            new XElement(Dav + "basicsearch",
                new XElement(Dav + "select", new XElement(Dav + "prop",
                    new XElement(Oc + "fileid"), new XElement(Dav + "resourcetype"), new XElement(Dav + "getcontentlength"))),
                new XElement(Dav + "from", new XElement(Dav + "scope",
                    new XElement(Dav + "href", $"/files/{Uri.EscapeDataString(userId)}"),
                    new XElement(Dav + "depth", "infinity"))),
                new XElement(Dav + "where", where))));

        using var request = new HttpRequestMessage(new HttpMethod("SEARCH"), "remote.php/dav/")
        {
            Content = new StringContent(search.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml"),
        };
        using var response = await SendAsync(request, userId, ct);
        EnsureSuccess(response, "SEARCH");
        var found = new List<(long, FilesEntry)>();
        foreach (var (entry, props) in await ReadMultistatusAsync(response, ct))
        {
            if (long.TryParse(props.Element(Oc + "fileid")?.Value, CultureInfo.InvariantCulture, out var id) && fileIds.Contains(id))
            {
                found.Add((id, entry));
            }
        }

        return found;
    }

    /// <summary>
    /// Saves a file into <paramref name="folder"/> without replacing anything: when the name is
    /// taken, "report (2).pdf" and so on. <paramref name="content"/> must be seekable, since a name
    /// taken in the meantime means sending it again.
    /// </summary>
    /// <returns>The path the file was saved under.</returns>
    public async Task<string> UploadAsync(
        string userId, string folder, string fileName, string contentType, Stream content, CancellationToken ct)
    {
        var folderNames = SplitPath(folder) ?? throw NextcloudFilesException.FolderNotFound();
        var name = CleanFileName(fileName);
        for (var attempt = 0; attempt < NameAttempts; attempt++)
        {
            var taken = await ListNamesAsync(userId, folderNames, ct);
            var free = FreeName(name, taken);
            string[] target = [.. folderNames, free];

            content.Position = 0;
            // Not disposed: that would close the caller's stream, which the next attempt sends again.
            var request = new HttpRequestMessage(HttpMethod.Put, FileUrl(userId, target))
            {
                Content = new StreamContent(content),
            };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var type)
                ? type
                : new MediaTypeHeaderValue("application/octet-stream");
            // Never overwrite: a file created since the listing makes this fail, and we try the next name.
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);

            using var response = await SendAsync(request, userId, ct);
            switch (response.StatusCode)
            {
                case HttpStatusCode.Created or HttpStatusCode.NoContent or HttpStatusCode.OK:
                    return JoinPath(target);
                case HttpStatusCode.PreconditionFailed:
                    continue;
                case HttpStatusCode.Forbidden:
                    throw new NextcloudFilesException("no_permission", "You cannot add files to this folder.", StatusCodes.Status403Forbidden);
                case HttpStatusCode.NotFound or HttpStatusCode.Conflict:
                    throw NextcloudFilesException.FolderNotFound();
                case HttpStatusCode.InsufficientStorage:
                    throw new NextcloudFilesException("quota", "There is not enough space left in your Files.", StatusCodes.Status507InsufficientStorage);
                default:
                    EnsureSuccess(response, "PUT");
                    return JoinPath(target);
            }
        }

        throw new NextcloudFilesException("name_conflict", "Could not find a free file name in this folder. Try again.", StatusCodes.Status409Conflict);
    }

    /// <summary>
    /// A public, read-only link to a file or folder, as the Files app's "Share link" creates it.
    /// Nextcloud's sharing settings apply (default expiry; refused when it enforces passwords).
    /// </summary>
    /// <returns>The link as Nextcloud wrote it (with the host the app reaches Nextcloud by) and its token.</returns>
    public async Task<(string Url, string Token)> CreateShareLinkAsync(string userId, string path, CancellationToken ct)
    {
        var names = SplitPath(path) is { Length: > 0 } n ? n : throw NextcloudFilesException.NotFound();
        System.Text.Json.JsonElement share;
        try
        {
            share = await nextcloud.OcsAsync(HttpMethod.Post, "apps/files_sharing/api/v1/shares",
                new { path = JoinPath(names), shareType = 3, permissions = 1 }, userId, ct);
        }
        catch (NextcloudApiException e) when (e.StatusCode == StatusCodes.Status404NotFound)
        {
            throw NextcloudFilesException.NotFound();
        }
        catch (NextcloudApiException e) when (e.StatusCode is >= 400 and < 500)
        {
            throw new NextcloudFilesException("share_refused",
                "Nextcloud did not create a public link for this file. Public links may be turned off, or need a password; share it from the Files app instead.",
                StatusCodes.Status403Forbidden);
        }
        catch (Exception e) when (e is NextcloudApiException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Creating a share link failed: {Error}", e.Message);
            throw NextcloudFilesException.Unavailable();
        }

        return share.ValueKind == System.Text.Json.JsonValueKind.Object
               && share.TryGetProperty("url", out var url) && url.GetString() is { Length: > 0 } link
               && share.TryGetProperty("token", out var token) && token.GetString() is { Length: > 0 } shareToken
            ? (link, shareToken)
            : throw NextcloudFilesException.Unavailable();
    }

    private async Task<HashSet<string>> ListNamesAsync(string userId, string[] folder, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), FileUrl(userId, folder).TrimEnd('/') + "/")
        {
            Content = new StringContent(
                """<?xml version="1.0"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/></d:prop></d:propfind>""",
                Encoding.UTF8, "text/xml"),
        };
        request.Headers.Add("Depth", "1");
        using var response = await SendAsync(request, userId, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            throw NextcloudFilesException.FolderNotFound();
        }

        EnsureSuccess(response, "PROPFIND");
        var entries = await ReadMultistatusAsync(response, ct);
        var self = entries.FirstOrDefault(e => e.Entry.Path.TrimEnd('/') == JoinPath(folder).TrimEnd('/'));
        if (self.Entry is { IsFolder: false })
        {
            throw NextcloudFilesException.FolderNotFound();
        }

        return entries.Where(e => e.Entry != self.Entry).Select(e => e.Entry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string userId, CancellationToken ct)
    {
        try
        {
            return await nextcloud.DavAsync(request, userId, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("WebDAV {Method} failed: {Error}", request.Method, e.Message);
            throw NextcloudFilesException.Unavailable();
        }
    }

    private void EnsureSuccess(HttpResponseMessage response, string method)
    {
        if (!response.IsSuccessStatusCode)
        {
            // Status only: WebDAV error bodies can echo paths.
            logger.LogWarning("WebDAV {Method} returned HTTP {Status}", method, (int)response.StatusCode);
            throw NextcloudFilesException.Unavailable();
        }
    }

    /// <summary>Each response of a 207 multistatus: the entry and its found (200) properties.</summary>
    private static async Task<List<(FilesEntry Entry, XElement Props)>> ReadMultistatusAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, ct);
        var result = new List<(FilesEntry, XElement)>();
        foreach (var item in document.Root?.Elements(Dav + "response") ?? [])
        {
            if (PathFromHref(item.Element(Dav + "href")?.Value) is not { } names)
            {
                continue;
            }

            var props = item.Elements(Dav + "propstat")
                .Where(p => p.Element(Dav + "status")?.Value.Contains(" 200 ") == true)
                .Select(p => p.Element(Dav + "prop"))
                .FirstOrDefault() ?? new XElement(Dav + "prop");
            var isFolder = props.Element(Dav + "resourcetype")?.Element(Dav + "collection") is not null;
            long? size = long.TryParse(props.Element(Dav + "getcontentlength")?.Value, CultureInfo.InvariantCulture, out var s) ? s : null;
            result.Add((new FilesEntry(JoinPath(names), names.Length > 0 ? names[^1] : "", isFolder, size), props));
        }

        return result;
    }

    /// <summary>"/nextcloud/remote.php/dav/files/alice/A%20b/c.pdf" → ["A b", "c.pdf"] (the webroot may vary).</summary>
    private static string[]? PathFromHref(string? href)
    {
        if (string.IsNullOrEmpty(href))
        {
            return null;
        }

        // Only http(s) URLs count as absolute: on Linux "/remote.php/…" parses as a file:// URI,
        // whose AbsolutePath escapes the "%" of the escapes again.
        var parts = (href.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                     && Uri.TryCreate(href, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : href)
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 3 < parts.Length; i++)
        {
            if (parts[i] == "remote.php" && parts[i + 1] == "dav" && parts[i + 2] == "files")
            {
                return parts[(i + 4)..].Select(Uri.UnescapeDataString).ToArray();
            }
        }

        return null;
    }

    private static string FileUrl(string userId, IEnumerable<string> names) =>
        $"remote.php/dav/files/{Uri.EscapeDataString(userId)}/{string.Join('/', names.Select(Uri.EscapeDataString))}";

    /// <summary>"report.pdf" when free, else "report (2).pdf", "report (3).pdf", …</summary>
    public static string FreeName(string name, IReadOnlySet<string> taken)
    {
        if (!taken.Contains(name))
        {
            return name;
        }

        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 ? (name[..dot], name[dot..]) : (name, "");
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){extension}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>A name Nextcloud accepts: no slashes, control characters or leading/trailing spaces, at most 250 characters.</summary>
    public static string CleanFileName(string? name)
    {
        var cleaned = string.Concat((name ?? "").Where(c => !char.IsControl(c) && c is not ('/' or '\\'))).Trim();
        if (cleaned.Length > 250)
        {
            var dot = cleaned.LastIndexOf('.');
            var extension = dot > 0 && cleaned.Length - dot <= 10 ? cleaned[dot..] : "";
            cleaned = cleaned[..(250 - extension.Length)] + extension;
        }

        return cleaned is "" or "." or ".." ? "attachment" : cleaned;
    }
}
