using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SocklessMail.Api.Mailbox;
using SocklessMail.Api.OAuth;

namespace SocklessMail.Api.Tags;

public sealed record GraphOptions
{
    /// <summary>Microsoft Graph's v1.0 endpoint; tests point it at a fake.</summary>
    public string BaseUrl { get; init; } = "https://graph.microsoft.com/v1.0";

    /// <summary>Most messages read per category in one reconcile.</summary>
    public int MaxMessagesPerCategory { get; init; } = 5000;
}

/// <summary>Graph could not be reached, throttled the app, or answered something unusable.</summary>
public sealed class GraphUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>An Outlook category of the master list; <see cref="Color"/> is "preset0" … "preset24" or "none".</summary>
public sealed record GraphCategory(string Id, string Name, string Color);

/// <summary>A message as Graph lists it, with the fields a tag folder shows.</summary>
public sealed record GraphMessage(
    string Id, string? InternetMessageId, IReadOnlyList<string> Categories, string Subject, AddressDto[] From, AddressDto[] To,
    DateTimeOffset Date, bool Read, bool Flagged, bool HasAttachments, string? Preview);

/// <summary>
/// The few Microsoft Graph calls tags need: the user's Outlook categories and the categories of
/// messages. Delegated tokens (see <see cref="MailLogins.GraphTokenAsync"/>); error bodies are not logged.
/// </summary>
public sealed class GraphClient(IHttpClientFactory http, GraphOptions options, ILogger<GraphClient> logger)
{
    public const string HttpClientName = "graph";

    private const string MessageFields = "id,internetMessageId,categories,subject,from,toRecipients,receivedDateTime,isRead,flag,hasAttachments,bodyPreview";

    public async Task<List<GraphCategory>> CategoriesAsync(string token, CancellationToken ct)
    {
        using var doc = await SendAsync(token, HttpMethod.Get, "/me/outlook/masterCategories", null, ct);
        return doc!.RootElement.GetProperty("value").EnumerateArray().Select(ReadCategory).ToList();
    }

    public async Task<GraphCategory> CreateCategoryAsync(string token, string name, string color, CancellationToken ct)
    {
        using var doc = await SendAsync(token, HttpMethod.Post, "/me/outlook/masterCategories", new { displayName = name, color }, ct);
        return ReadCategory(doc!.RootElement);
    }

    public async Task SetCategoryColorAsync(string token, string id, string color, CancellationToken ct) =>
        (await SendAsync(token, HttpMethod.Patch, $"/me/outlook/masterCategories/{Uri.EscapeDataString(id)}", new { color }, ct))?.Dispose();

    public async Task DeleteCategoryAsync(string token, string id, CancellationToken ct) =>
        (await SendAsync(token, HttpMethod.Delete, $"/me/outlook/masterCategories/{Uri.EscapeDataString(id)}", null, ct))?.Dispose();

    /// <summary>Every copy of a message (a Message-ID may be in several folders).</summary>
    public Task<List<GraphMessage>> MessagesByInternetIdAsync(string token, string messageIdHeader, CancellationToken ct) =>
        ListAsync(token, $"internetMessageId eq '{Literal($"<{messageIdHeader}>")}'", 50, ct);

    public Task<List<GraphMessage>> MessagesWithCategoryAsync(string token, string category, CancellationToken ct) =>
        ListAsync(token, $"categories/any(c:c eq '{Literal(category)}')", options.MaxMessagesPerCategory, ct);

    public async Task SetMessageCategoriesAsync(string token, string id, IReadOnlyList<string> categories, CancellationToken ct) =>
        (await SendAsync(token, HttpMethod.Patch, $"/me/messages/{Uri.EscapeDataString(id)}", new { categories }, ct))?.Dispose();

    private async Task<List<GraphMessage>> ListAsync(string token, string filter, int max, CancellationToken ct)
    {
        var messages = new List<GraphMessage>();
        string? next = $"/me/messages?$filter={Uri.EscapeDataString(filter)}&$select={MessageFields}&$top=100";
        while (next is not null && messages.Count < max)
        {
            using var doc = await SendAsync(token, HttpMethod.Get, next, null, ct);
            messages.AddRange(doc!.RootElement.GetProperty("value").EnumerateArray().Select(ReadMessage));
            next = doc.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        }

        return messages;
    }

    /// <summary>An OData string literal: single quotes doubled.</summary>
    private static string Literal(string value) => value.Replace("'", "''");

    /// <param name="path">Relative to <see cref="GraphOptions.BaseUrl"/>, or an absolute next link.</param>
    /// <returns>The answer's JSON; null for an empty answer.</returns>
    private async Task<JsonDocument?> SendAsync(string token, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var url = path.StartsWith("https://", StringComparison.Ordinal) || path.StartsWith("http://", StringComparison.Ordinal)
            ? path
            : options.BaseUrl.TrimEnd('/') + path;
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new GraphUnavailableException("Microsoft Graph could not be reached.", e);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Forbidden)
            {
                // The token lacks the permission: the app registration or the consent is missing it.
                throw new GraphConsentRequiredException();
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Microsoft Graph answered {StatusCode} to {Method}", (int)response.StatusCode, method);
                throw new GraphUnavailableException(response.StatusCode == HttpStatusCode.TooManyRequests
                    ? "Microsoft Graph asks to slow down. Try again in a minute."
                    : $"Microsoft Graph refused the change (HTTP {(int)response.StatusCode}).");
            }

            var text = await response.Content.ReadAsStringAsync(ct);
            try
            {
                return text.Length == 0 ? null : JsonDocument.Parse(text);
            }
            catch (JsonException e)
            {
                throw new GraphUnavailableException("Microsoft Graph answered something unexpected.", e);
            }
        }
    }

    private static GraphCategory ReadCategory(JsonElement e) =>
        new(Text(e, "id") ?? "", Text(e, "displayName") ?? "", Text(e, "color") ?? "none");

    private static GraphMessage ReadMessage(JsonElement e)
    {
        static AddressDto? Address(JsonElement recipient) =>
            recipient.ValueKind == JsonValueKind.Object && recipient.TryGetProperty("emailAddress", out var a)
                ? new AddressDto(Text(a, "name") is { Length: > 0 } name ? name : null, Text(a, "address") ?? "")
                : null;

        return new GraphMessage(
            Text(e, "id") ?? "",
            Text(e, "internetMessageId"),
            e.TryGetProperty("categories", out var c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [],
            Text(e, "subject") ?? "",
            e.TryGetProperty("from", out var from) && Address(from) is { } sender ? [sender] : [],
            e.TryGetProperty("toRecipients", out var to) && to.ValueKind == JsonValueKind.Array ? to.EnumerateArray().Select(Address).OfType<AddressDto>().ToArray() : [],
            e.TryGetProperty("receivedDateTime", out var d) && d.TryGetDateTimeOffset(out var date) ? date : DateTimeOffset.UnixEpoch,
            e.TryGetProperty("isRead", out var r) && r.ValueKind == JsonValueKind.True,
            e.TryGetProperty("flag", out var f) && Text(f, "flagStatus") == "flagged",
            e.TryGetProperty("hasAttachments", out var h) && h.ValueKind == JsonValueKind.True,
            Text(e, "bodyPreview"));
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Outlook's 25 category colors and the nearest one to a tag's color.</summary>
public static class OutlookColors
{
    /// <summary>preset0 … preset24, as Outlook on the web shows them (approximately).</summary>
    private static readonly string[] Presets =
    [
        "#e81123", "#ff8c00", "#a0522d", "#ffc83d", "#13a10e", "#00b7c3", "#849e35", "#0078d4", "#8764b8", "#a4262c",
        "#69797e", "#374649", "#a0aeb2", "#4c4c4c", "#000000", "#750b1c", "#ca5010", "#5c3b1b", "#c19c00", "#0b6a0b",
        "#005e50", "#4b5320", "#004e8c", "#32145a", "#6e0811",
    ];

    public static string Nearest(string color)
    {
        var (r, g, b) = Rgb(color);
        // "Redmean" weighting: closer to how far apart colors look than plain RGB distance.
        var best = Enumerable.Range(0, Presets.Length).MinBy(i =>
        {
            var (pr, pg, pb) = Rgb(Presets[i]);
            var mean = (r + pr) / 2.0;
            return (2 + mean / 256) * (r - pr) * (r - pr) + 4.0 * (g - pg) * (g - pg) + (2 + (255 - mean) / 256) * (b - pb) * (b - pb);
        });
        return $"preset{best}";
    }

    /// <summary>A preset as a tag color; null for "none" or anything unknown.</summary>
    public static string? ToHex(string preset) =>
        preset.StartsWith("preset", StringComparison.Ordinal) && int.TryParse(preset.AsSpan(6), out var i) && i >= 0 && i < Presets.Length
            ? Presets[i]
            : null;

    private static (int R, int G, int B) Rgb(string color)
    {
        var value = Convert.ToInt32(color.TrimStart('#'), 16);
        return ((value >> 16) & 255, (value >> 8) & 255, value & 255);
    }
}
