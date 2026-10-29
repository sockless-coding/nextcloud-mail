using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Xml.Linq;
using SocklessMail.Api.AppApi;

namespace SocklessMail.Api.Calendar;

/// <summary>An event calendar of the user, by its name in their calendar home ("personal").</summary>
public sealed record CalendarInfo(string Id, string Name, string? Color, bool Writable);

/// <summary>A stored calendar object: where it is, its ETag and its text.</summary>
public sealed record StoredObject(CalendarInfo Calendar, string Name, string? ETag, string Text);

/// <summary>What a calendar operation could not do, in words the user can act on.</summary>
public sealed class CalendarException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;

    public int StatusCode { get; } = statusCode;

    public static CalendarException Unavailable() =>
        new("nextcloud_unavailable", "Nextcloud Calendar did not answer. Try again in a moment.", StatusCodes.Status502BadGateway);

    public static CalendarException Changed() =>
        new("changed", "The event was changed in the calendar in the meantime. Try again.", StatusCodes.Status409Conflict);
}

/// <summary>
/// The user's Nextcloud calendars over CalDAV (remote.php/dav/calendars/&lt;user&gt;), as that
/// user through AppAPI. Subscriptions, task-only calendars and the scheduling inbox/outbox and
/// trash bin are left out.
/// </summary>
public sealed class NextcloudCalendars(NextcloudClient nextcloud, ILogger<NextcloudCalendars> logger)
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace Apple = "http://apple.com/ns/ical/";

    public async Task<IReadOnlyList<CalendarInfo>> ListAsync(string userId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), HomeUrl(userId))
        {
            Content = Xml("""<d:propfind xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav" xmlns:a="http://apple.com/ns/ical/"><d:prop><d:resourcetype/><d:displayname/><a:calendar-color/><c:supported-calendar-component-set/><d:current-user-privilege-set/></d:prop></d:propfind>"""),
        };
        request.Headers.Add("Depth", "1");
        var calendars = new List<CalendarInfo>();
        foreach (var (href, props) in await MultistatusAsync(request, userId, ct))
        {
            var type = props.Element(Dav + "resourcetype");
            if (type?.Element(CalDav + "calendar") is null || type.Elements().Any(e => e.Name.LocalName == "subscribed")
                || LastSegment(href) is not { } id)
            {
                continue;
            }

            var components = props.Element(CalDav + "supported-calendar-component-set")?.Elements(CalDav + "comp")
                .Select(c => c.Attribute("name")?.Value).ToList();
            if (components is { Count: > 0 } && !components.Contains("VEVENT"))
            {
                continue;
            }

            var privileges = props.Element(Dav + "current-user-privilege-set")?.Elements(Dav + "privilege")
                .SelectMany(p => p.Elements()).Select(e => e.Name.LocalName).ToHashSet() ?? [];
            var name = props.Element(Dav + "displayname")?.Value;
            var color = props.Element(Apple + "calendar-color")?.Value.Trim();
            calendars.Add(new CalendarInfo(id, string.IsNullOrWhiteSpace(name) ? id : name,
                color is { Length: >= 7 } && color[0] == '#' ? color[..7] : null,
                privileges.Contains("all") || privileges.Contains("bind") || privileges.Contains("write-content")));
        }

        return calendars;
    }

    /// <summary>The object with this UID in any of the calendars, or null.</summary>
    public async Task<StoredObject?> FindAsync(string userId, IReadOnlyList<CalendarInfo> calendars, string uid, CancellationToken ct)
    {
        foreach (var calendar in calendars)
        {
            using var request = new HttpRequestMessage(new HttpMethod("REPORT"), CalendarUrl(userId, calendar.Id))
            {
                Content = Xml($"""<c:calendar-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop><d:getetag/><c:calendar-data/></d:prop><c:filter><c:comp-filter name="VCALENDAR"><c:comp-filter name="VEVENT"><c:prop-filter name="UID"><c:text-match collation="i;octet">{SecurityElement.Escape(uid)}</c:text-match></c:prop-filter></c:comp-filter></c:comp-filter></c:filter></c:calendar-query>"""),
            };
            request.Headers.Add("Depth", "1");
            List<(string, XElement)> found;
            try
            {
                found = await MultistatusAsync(request, userId, ct);
            }
            catch (CalendarException e) when (e.Code == "not_found")
            {
                continue;
            }

            foreach (var (href, props) in found)
            {
                var text = props.Element(CalDav + "calendar-data")?.Value;
                if (text is null || LastSegment(href) is not { } name)
                {
                    continue;
                }

                // text-match is a substring match; the UID must be equal.
                try
                {
                    if (ICalComponent.Parse(text).ChildrenNamed("VEVENT").Any(e => e.Property("UID")?.Value.Trim() == uid))
                    {
                        return new StoredObject(calendar, name, props.Element(Dav + "getetag")?.Value, text);
                    }
                }
                catch (FormatException)
                {
                    // Unreadable object: not the one we look for.
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Writes an object: replacing the version with <paramref name="etag"/>, or with
    /// <paramref name="create"/> only if there is none yet. Returns the new ETag when Nextcloud says it.
    /// </summary>
    public async Task<string?> PutAsync(string userId, string calendarId, string name, string text, string? etag, bool create, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, ObjectUrl(userId, calendarId, name))
        {
            Content = new StringContent(text, Encoding.UTF8, "text/calendar"),
        };
        if (create)
        {
            request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        }
        else if (etag is not null && EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            request.Headers.IfMatch.Add(tag);
        }

        using var response = await SendAsync(request, userId, ct);
        ThrowForWrite(response, "PUT");
        return response.Headers.ETag?.ToString();
    }

    public async Task DeleteAsync(string userId, string calendarId, string name, string? etag, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, ObjectUrl(userId, calendarId, name));
        if (etag is not null && EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            request.Headers.IfMatch.Add(tag);
        }

        using var response = await SendAsync(request, userId, ct);
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            ThrowForWrite(response, "DELETE");
        }
    }

    /// <summary>A file name for a new object: the UID when it is a safe one, else a random one.</summary>
    public static string ObjectName(string uid) =>
        uid.Length is > 0 and <= 200 && uid.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '@') && uid[0] != '.'
            ? uid + ".ics"
            : Guid.NewGuid() + ".ics";

    public static bool ValidSegment(string? segment) =>
        !string.IsNullOrEmpty(segment) && segment.Length <= 255 && segment is not ("." or "..")
        && !segment.Any(c => char.IsControl(c) || c is '/' or '\\');

    private void ThrowForWrite(HttpResponseMessage response, string method)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.Forbidden or HttpStatusCode.MethodNotAllowed:
                throw new CalendarException("read_only", "This calendar is read-only.", StatusCodes.Status403Forbidden);
            case HttpStatusCode.PreconditionFailed:
                throw CalendarException.Changed();
            case HttpStatusCode.NotFound or HttpStatusCode.Conflict:
                throw new CalendarException("not_found", "The calendar no longer exists.", StatusCodes.Status404NotFound);
            case HttpStatusCode.InsufficientStorage:
                throw new CalendarException("quota", "There is not enough space left in your Nextcloud.", StatusCodes.Status507InsufficientStorage);
            default:
                if (!response.IsSuccessStatusCode)
                {
                    // Sabre refuses invalid calendar data with 400/415.
                    logger.LogWarning("CalDAV {Method} returned HTTP {Status}", method, (int)response.StatusCode);
                    throw response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnsupportedMediaType
                        ? new CalendarException("invalid", "Nextcloud did not accept this event.", StatusCodes.Status422UnprocessableEntity)
                        : CalendarException.Unavailable();
                }

                break;
        }
    }

    private async Task<List<(string Href, XElement Props)>> MultistatusAsync(HttpRequestMessage request, string userId, CancellationToken ct)
    {
        using var response = await SendAsync(request, userId, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            throw new CalendarException("not_found", "The calendar no longer exists.", StatusCodes.Status404NotFound);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("CalDAV {Method} returned HTTP {Status}", request.Method, (int)response.StatusCode);
            throw CalendarException.Unavailable();
        }

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
            logger.LogWarning("CalDAV {Method} failed: {Error}", request.Method, e.Message);
            throw CalendarException.Unavailable();
        }
    }

    private static StringContent Xml(string body) => new("""<?xml version="1.0"?>""" + body, Encoding.UTF8, "text/xml");

    private static string? LastSegment(string href)
    {
        var path = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(href, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : href;
        var decoded = Uri.UnescapeDataString(path.TrimEnd('/').Split('/')[^1]);
        return ValidSegment(decoded) ? decoded : null;
    }

    private static string HomeUrl(string userId) => $"remote.php/dav/calendars/{Uri.EscapeDataString(userId)}/";

    private static string CalendarUrl(string userId, string calendarId) =>
        ValidSegment(calendarId)
            ? $"{HomeUrl(userId)}{Uri.EscapeDataString(calendarId)}/"
            : throw new CalendarException("not_found", "The calendar no longer exists.", StatusCodes.Status404NotFound);

    private static string ObjectUrl(string userId, string calendarId, string name) =>
        ValidSegment(name)
            ? CalendarUrl(userId, calendarId) + Uri.EscapeDataString(name)
            : throw new CalendarException("not_found", "The event no longer exists.", StatusCodes.Status404NotFound);
}
