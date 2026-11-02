using MailKit;
using MimeKit;
using MimeKit.IO;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Crypto;

namespace SocklessMail.Api.Mailbox;

/// <summary>
/// Encrypted and signed messages: fetched whole, opened with <see cref="MessageSecurity"/>, and
/// shown from the decrypted MIME tree. Parts of that tree have ids starting with "c" (their path in
/// it), since the server's part numbers only reach the ciphertext.
/// </summary>
public sealed partial class MailboxService
{
    /// <summary>What the reader shows of a decrypted or signed message.</summary>
    private sealed record SecureView(string Html, IReadOnlyDictionary<string, string> InlineParts, AttachmentDto[] Attachments, CalendarPartDto[] CalendarParts, string? Subject);

    private const string SecurePartPrefix = "c";

    private static bool IsSecurePartId(string partId) => partId.StartsWith(SecurePartPrefix, StringComparison.Ordinal);

    /// <summary>How the message's structure says it is protected, and in which standard ("openpgp" or "smime").</summary>
    private static (SecureShape Shape, string Kind) SecureShapeOf(BodyPart? body)
    {
        if (body is null)
        {
            return (SecureShape.None, "");
        }

        var type = body.ContentType;
        var shape = MessageSecurity.ShapeOf(type.MediaType, type.MediaSubtype, type.Parameters["smime-type"]);
        var protocol = type.Parameters["protocol"]?.ToLowerInvariant() ?? "";
        return (shape, protocol.Contains("pgp") ? "openpgp" : "smime");
    }

    /// <summary>The message opened (decrypted, signatures checked), from the short-lived cache or fetched whole now.</summary>
    private async Task<OpenedMessage> OpenSecureAsync(MailAccount account, string folderId, string messageId, IMailFolder folder, UniqueId uid, CancellationToken ct)
    {
        var key = DecryptedMessages.Key(account.Id, folderId, messageId);
        if (decrypted.Find(account.OwnerUserId, key) is { } known)
        {
            return known;
        }

        var message = await folder.GetMessageAsync(uid, ct);
        var opened = await security.OpenAsync(account.OwnerUserId, message, ct);
        if (opened.Body is not null)
        {
            decrypted.Keep(account.OwnerUserId, key, opened, await MeasureAsync(message, ct));
        }

        return opened;
    }

    private static async Task<long> MeasureAsync(MimeMessage message, CancellationToken ct)
    {
        await using var measure = new MeasuringStream();
        await message.WriteToAsync(measure, ct);
        return measure.Length;
    }

    /// <summary>The opened message for a "c" part id, from the cache or fetched again.</summary>
    private Task<OpenedMessage> OpenSecureAsync(MailAccount account, string folderId, string messageId, CancellationToken ct)
    {
        if (decrypted.Find(account.OwnerUserId, DecryptedMessages.Key(account.Id, folderId, messageId)) is { } known)
        {
            return Task.FromResult(known);
        }

        return Run(account, async (client, token) =>
        {
            var (folder, uid) = await OpenMessageAsync(client, folderId, messageId, token);
            var summary = await FetchOneAsync(folder, uid, MessageSummaryItems.UniqueId | MessageSummaryItems.Size, token);
            if ((summary.Size ?? 0) > MessageSecurity.MaxBytes)
            {
                throw new MailboxNotFoundException("The message part");
            }

            return await OpenSecureAsync(account, folderId, messageId, folder, uid, token);
        }, ct);
    }

    /// <summary>The leaf part of an opened message with this "c" id.</summary>
    private async Task<MimeEntity> SecurePartAsync(MailAccount account, string folderId, string messageId, string partId, CancellationToken ct)
    {
        var opened = await OpenSecureAsync(account, folderId, messageId, ct);
        return opened.Body is { } body && SecureLeaves(body).FirstOrDefault(l => l.Id == partId).Entity is { } entity
            ? entity
            : throw new MailboxNotFoundException("The message part");
    }

    /// <summary>Leaf parts with their ids: "c" and the part's path in the tree ("c1.2"), "c0" for a body that is one part.</summary>
    private static IEnumerable<(string Id, MimeEntity Entity)> SecureLeaves(MimeEntity entity, string path = "")
    {
        if (entity is Multipart multipart)
        {
            for (var i = 0; i < multipart.Count; i++)
            {
                foreach (var leaf in SecureLeaves(multipart[i], path.Length == 0 ? $"{i + 1}" : $"{path}.{i + 1}"))
                {
                    yield return leaf;
                }
            }

            yield break;
        }

        yield return (SecurePartPrefix + (path.Length == 0 ? "0" : path), entity);
    }

    private static SecureView ViewOf(MimeEntity body)
    {
        var leaves = SecureLeaves(body).ToList();
        var text = FindBody(body, html: true) ?? FindBody(body, html: false);
        var html = text is null ? "" : text.IsHtml ? text.Text : PlainTextToHtml(text);

        var inline = leaves
            .Where(l => !string.IsNullOrEmpty(l.Entity.ContentId))
            .GroupBy(l => l.Entity.ContentId!.Trim('<', '>'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var attachments = leaves
            .Where(l => l.Entity.IsAttachment || l.Entity is MessagePart)
            .Select(l => new AttachmentDto(l.Id, FileNameOf(l.Entity, l.Id), l.Entity.ContentType.MimeType.ToLowerInvariant(), DecodedSize(l.Entity)))
            .ToArray();

        var calendar = leaves
            .Where(l => IsCalendarPart(l.Entity.ContentType, (l.Entity as MimePart)?.FileName))
            .Select(l => new CalendarPartDto(l.Id, l.Entity.ContentType.Parameters["method"]?.Trim().ToUpperInvariant()))
            .ToArray();

        // Protected headers (draft-autocrypt-lamps-protected-headers): the real subject travels inside.
        var subject = body.ContentType.Parameters["protected-headers"] is not null ? body.Headers[HeaderId.Subject] : null;
        return new SecureView(html, inline, attachments, calendar, string.IsNullOrWhiteSpace(subject) ? null : subject.Trim());
    }

    /// <summary>The text to show: in an alternative the richest part we can, in a related its root, otherwise the first that is not an attachment.</summary>
    private static TextPart? FindBody(MimeEntity entity, bool html)
    {
        switch (entity)
        {
            case MultipartAlternative alternative:
                for (var i = alternative.Count - 1; i >= 0; i--)
                {
                    if (FindBody(alternative[i], html) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            case MultipartRelated related:
                return related.Root is { } root ? FindBody(root, html) : null;
            case Multipart multipart:
                foreach (var part in multipart)
                {
                    if (FindBody(part, html) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            case TextPart text when !text.IsAttachment && (html ? text.IsHtml : text.IsPlain):
                return text;
            default:
                return null;
        }
    }

    private static long? DecodedSize(MimeEntity entity)
    {
        using var measure = new MeasuringStream();
        switch (entity)
        {
            case MimePart { Content: { } content }:
                content.DecodeTo(measure);
                return measure.Length;
            case MessagePart { Message: { } message }:
                message.WriteTo(measure);
                return measure.Length;
            default:
                return null;
        }
    }

    private static PartContent SecurePartContent(string partId, MimeEntity entity) => entity switch
    {
        MimePart mime => new PartContent(FileNameOf(mime, partId), mime.ContentType.MimeType.ToLowerInvariant(),
            (stream, t) => mime.Content?.DecodeToAsync(stream, t) ?? Task.CompletedTask),
        MessagePart message => new PartContent(FileNameOf(message, partId), "message/rfc822",
            (stream, t) => message.Message?.WriteToAsync(stream, t) ?? Task.CompletedTask),
        _ => throw new MailboxNotFoundException("The message part"),
    };

    private static string FileNameOf(MimeEntity entity, string partId) =>
        CleanFileName((entity as MimePart)?.FileName ?? entity.ContentType.Name, entity is MessagePart, partId);

    /// <summary>What the reader says when the message could not be opened, or was too large to try.</summary>
    private static MessageSecurityDto TooLarge(SecureShape shape, string kind) => shape == SecureShape.Encrypted
        ? new MessageSecurityDto(new EncryptionDto(kind, "too_large", []), [])
        : new MessageSecurityDto(null, [new SignatureDto(kind, "not_checked", null, [], null, null, null, false, null)]);
}
