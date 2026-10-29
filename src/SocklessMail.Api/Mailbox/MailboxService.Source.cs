using System.Text;
using MailKit;
using SocklessMail.Api.Accounts;

namespace SocklessMail.Api.Mailbox;

/// <summary>
/// A message as the server stores it: its raw header block, its source for reading, and the
/// whole message as an .eml download. Fetched with BODY.PEEK, so none of this sets \Seen, and
/// never cached: these views should show what the server has.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>The source view shows at most this much; the download has the rest.</summary>
    public const int MaxSourceViewBytes = 1024 * 1024;

    private const int MaxFileNameLength = 80;

    public Task<MessageHeadersDto> GetHeadersAsync(MailAccount account, string folderId, string messageId, CancellationToken ct) =>
        Run(account, async (client, token) =>
        {
            var (folder, uid) = await OpenMessageAsync(client, folderId, messageId, token);
            var bytes = await ReadSectionAsync(folder, uid, "HEADER", null, token);
            return new MessageHeadersDto(DecodeRaw(bytes, truncated: false));
        }, ct);

    public Task<MessageSourceDto> GetSourceAsync(MailAccount account, string folderId, string messageId, CancellationToken ct) =>
        Run(account, async (client, token) =>
        {
            var (folder, uid) = await OpenMessageAsync(client, folderId, messageId, token);
            var summary = await FetchOneAsync(folder, uid, MessageSummaryItems.UniqueId | MessageSummaryItems.Size, token);
            long size = summary.Size ?? 0;
            var truncated = size > MaxSourceViewBytes;
            var bytes = await ReadSectionAsync(folder, uid, "", truncated ? MaxSourceViewBytes : null, token);
            return new MessageSourceDto(DecodeRaw(bytes, truncated), size > 0 ? size : bytes.Length, truncated);
        }, ct);

    public Task<PartContent> GetRawMessageAsync(MailAccount account, string folderId, string messageId, CancellationToken ct) =>
        Run(account, async (client, token) =>
        {
            var (folder, uid) = await OpenMessageAsync(client, folderId, messageId, token);
            var summary = await FetchOneAsync(folder, uid, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope, token);
            // Loaded into memory, like a part, so the download streams after the connection went back to the pool.
            var bytes = await ReadSectionAsync(folder, uid, "", null, token);
            return new PartContent(EmlFileName(summary.Envelope?.Subject), "message/rfc822",
                (stream, t) => stream.WriteAsync(bytes, t).AsTask());
        }, ct);

    /// <summary>BODY.PEEK[section], or its first <paramref name="count"/> bytes.</summary>
    private static async Task<byte[]> ReadSectionAsync(IMailFolder folder, UniqueId uid, string section, int? count, CancellationToken ct)
    {
        var content = new MemoryStream();
        await using (var stream = count is { } n
                         ? await folder.GetStreamAsync(uid, section, 0, n, ct)
                         : await folder.GetStreamAsync(uid, section, ct))
        {
            await stream.CopyToAsync(content, ct);
        }

        return content.ToArray();
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Raw mail is mostly ASCII, 8-bit parts are mostly UTF-8, and the rest is mostly Latin-1:
    /// UTF-8 if the bytes are valid UTF-8, Latin-1 otherwise, which maps every byte to a character.
    /// A truncated source may end inside a UTF-8 sequence; that partial sequence is dropped.
    /// </summary>
    internal static string DecodeRaw(byte[] bytes, bool truncated)
    {
        var length = bytes.Length;
        if (truncated)
        {
            var lead = length;
            while (lead > 0 && length - lead < 3 && (bytes[lead - 1] & 0xC0) == 0x80)
            {
                lead--;
            }

            if (lead > 0 && (bytes[lead - 1] & 0xC0) == 0xC0)
            {
                var first = bytes[lead - 1];
                var expected = first >= 0xF0 ? 4 : first >= 0xE0 ? 3 : 2;
                if (length - lead + 1 < expected)
                {
                    length = lead - 1;
                }
            }
        }

        try
        {
            return StrictUtf8.GetString(bytes, 0, length);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes, 0, bytes.Length);
        }
    }

    /// <summary>The subject as a file name every common file system accepts.</summary>
    internal static string EmlFileName(string? subject)
    {
        var name = new StringBuilder();
        foreach (var c in subject ?? "")
        {
            name.Append(c < 0x20 || "\\/:*?\"<>|".Contains(c) ? '_' : c);
        }

        var trimmed = name.ToString().Trim().TrimEnd('.');
        if (trimmed.Length > MaxFileNameLength)
        {
            var cut = char.IsHighSurrogate(trimmed[MaxFileNameLength - 1]) ? MaxFileNameLength - 1 : MaxFileNameLength;
            trimmed = trimmed[..cut].TrimEnd();
        }

        return (trimmed.Length > 0 ? trimmed : "message") + ".eml";
    }
}
