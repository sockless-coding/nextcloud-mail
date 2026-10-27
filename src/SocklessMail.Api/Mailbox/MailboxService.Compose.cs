using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MimeKit;
using MimeKit.Utils;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Compose;
using SocklessMail.Api.Mail;
using SocklessMail.Api.Sync;

namespace SocklessMail.Api.Mailbox;

/// <summary>Writing mail: reply/forward drafts and sending with a copy in Sent.</summary>
public sealed partial class MailboxService
{
    public const int MaxRecipients = 100;
    public const int MaxAttachments = 25;
    public const long MaxAttachmentBytes = 25 * 1024 * 1024;
    public const int MaxTextLength = 1024 * 1024;

    public Task<DraftDto> GetDraftAsync(MailAccount account, string folderId, string messageId, ReplyMode mode, CancellationToken ct) =>
        Run(account, async (client, token) =>
        {
            var (folder, uid) = await OpenMessageAsync(client, folderId, messageId, token);
            var summary = await FetchOneAsync(folder, uid, FolderCache.ListItems, token);
            var envelope = summary.Envelope ?? new Envelope();
            var body = await BodyTextAsync(folder, uid, summary, token);

            var (to, cc) = ReplyBuilder.Recipients(mode, envelope.From, envelope.ReplyTo, envelope.To, envelope.Cc, account.EmailAddress);
            return new DraftDto(
                to.ToString(),
                cc.ToString(),
                ReplyBuilder.Subject(mode, envelope.Subject),
                ReplyBuilder.QuotedText(mode, MessageDate(summary), envelope.From, envelope.To, envelope.Cc, envelope.Subject, body),
                mode == ReplyMode.Forward ? summary.Attachments.Select(ToAttachmentDto).ToArray() : [],
                new DraftSource(folderId, messageId, mode));
        }, ct);

    public async Task<SendResult> SendAsync(MailAccount account, SendInput input, CancellationToken ct)
    {
        var to = ParseAddresses(input.To, "to");
        var cc = ParseAddresses(input.Cc, "cc");
        var bcc = ParseAddresses(input.Bcc, "bcc");
        var recipients = to.Count + cc.Count + bcc.Count;
        if (recipients == 0)
        {
            throw new MailboxValidationException("to", "Add at least one recipient.");
        }

        if (recipients > MaxRecipients)
        {
            throw new MailboxValidationException("to", $"At most {MaxRecipients} recipients per message.");
        }

        var subject = (input.Subject ?? "").ReplaceLineEndings(" ").Trim();
        if (subject.Length > 998)
        {
            throw new MailboxValidationException("subject", "The subject is too long.");
        }

        var text = input.Text ?? "";
        if (text.Length > MaxTextLength)
        {
            throw new MailboxValidationException("text", "The message is too long.");
        }

        var attachmentIds = input.AttachmentIds ?? [];
        var forwardPartIds = input.Source is { Mode: ReplyMode.Forward } ? input.Source.ForwardPartIds ?? [] : [];
        if (attachmentIds.Count + forwardPartIds.Count > MaxAttachments)
        {
            throw new MailboxValidationException("attachmentIds", $"At most {MaxAttachments} attachments per message.");
        }

        var uploaded = new List<UploadedAttachment>();
        foreach (var id in attachmentIds.Distinct())
        {
            uploaded.Add(await uploads.GetAsync(account.OwnerUserId, id, ct)
                ?? throw new MailboxValidationException("attachmentIds", "An attachment has expired or was removed. Attach it again."));
        }

        var credential = credentials.Unprotect(account.Id, account.ProtectedCredential);
        var original = input.Source is { } source ? await LoadSourceAsync(account, source, forwardPartIds, ct) : null;

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(account.SenderName, account.EmailAddress));
        message.To.AddRange(to);
        message.Cc.AddRange(cc);
        message.Bcc.AddRange(bcc);
        message.Subject = subject;
        message.Date = clock.GetUtcNow();
        message.MessageId = MimeUtils.GenerateMessageId(account.EmailAddress.Split('@')[^1]);
        if (original is { MessageId: { } inReplyTo } && input.Source!.Mode != ReplyMode.Forward)
        {
            // Threading: the replied-to message and its own ancestors.
            message.InReplyTo = inReplyTo;
            message.References.AddRange(original.References);
            message.References.Add(inReplyTo);
        }

        var builder = new BodyBuilder { TextBody = text };
        var streams = new List<Stream>();
        try
        {
            long total = 0;
            foreach (var upload in uploaded)
            {
                var stream = uploads.OpenRead(account.OwnerUserId, upload);
                streams.Add(stream);
                total += upload.Size;
                builder.Attachments.Add(upload.FileName, stream, ContentType.Parse(upload.ContentType));
            }

            foreach (var part in original?.ForwardedParts ?? [])
            {
                total += part.Size;
                builder.Attachments.Add(part.Entity);
            }

            if (total > MaxAttachmentBytes)
            {
                throw new MailboxValidationException("attachmentIds", $"Attachments may total at most {MaxAttachmentBytes / (1024 * 1024)} MB.");
            }

            message.Body = builder.ToMessageBody();
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }
        }

        await DeliverAsync(account, credential, message, ct);
        logger.LogInformation("Sent message from account {AccountId} to {RecipientCount} recipients", account.Id, recipients);

        foreach (var upload in uploaded)
        {
            uploads.Delete(account.OwnerUserId, upload.Id);
        }

        return await AfterSendAsync(account, message, input.Source, ct);
    }

    private async Task DeliverAsync(MailAccount account, MailCredential credential, MimeMessage message, CancellationToken ct)
    {
        var endpoint = new MailEndpoint(account.SmtpHost, account.SmtpPort, account.SmtpSecurity);
        using var smtp = await connector.ConnectSmtpAsync(endpoint, account.Username, credential, ct);
        try
        {
            // Bcc recipients get the message, but nobody sees the Bcc header; the copy in Sent keeps it.
            var options = FormatOptions.Default.Clone();
            options.HiddenHeaders.Add(HeaderId.Bcc);
            await smtp.SendAsync(options, message, ct);
        }
        catch (SmtpCommandException e) when (e.ErrorCode == SmtpErrorCode.RecipientNotAccepted)
        {
            throw new MailboxValidationException("to", $"The server refused the recipient {e.Mailbox?.Address}.");
        }
        catch (SmtpCommandException e) when (e.ErrorCode == SmtpErrorCode.SenderNotAccepted)
        {
            throw new MailConnectionException("sender_rejected", $"The server does not accept {account.EmailAddress} as the sender.", e);
        }
        catch (SmtpCommandException e)
        {
            throw new MailConnectionException("message_rejected", "The server refused the message (it may be too large).", e);
        }
        catch (Exception e) when (e is SmtpProtocolException or IOException)
        {
            // The connection broke during the transfer: the message may or may not have gone out.
            throw new MailConnectionException("send_interrupted", "The connection broke while sending. Check Sent before trying again.", e);
        }

        try
        {
            await smtp.DisconnectAsync(true, ct);
        }
        catch (Exception e) when (e is IOException or SmtpProtocolException)
        {
            // Sent already; a failing QUIT does not matter.
        }
    }

    /// <summary>Best effort: a failure here must not look like a failed send (which the user might repeat).</summary>
    private async Task<SendResult> AfterSendAsync(MailAccount account, MimeMessage message, SendSource? source, CancellationToken ct)
    {
        try
        {
            return await Run(account, async (client, token) =>
            {
                string? warning = null;
                var sent = await FindSpecialFolderAsync(client, "sent", token);
                if (sent is not null)
                {
                    await sent.AppendAsync(message, MessageFlags.Seen, token);
                }
                else
                {
                    warning = "The message was sent, but this account has no Sent folder to keep a copy in.";
                }

                if (source is not null)
                {
                    await MarkOriginalAsync(client, source, token);
                }

                return new SendResult(sent is not null, sent is null ? null : MailboxIds.FolderId(sent.FullName), warning);
            }, ct, retry: false);
        }
        catch (Exception e) when (e is MailConnectionException or ImapCommandException or IOException or MailboxNotFoundException)
        {
            logger.LogWarning("Sent message, but saving it to Sent failed ({ExceptionType})", e.GetType().Name);
            return new SendResult(false, null, "The message was sent, but the copy could not be saved to Sent.");
        }
    }

    private static async Task MarkOriginalAsync(ImapClient client, SendSource source, CancellationToken ct)
    {
        try
        {
            var (folder, uid) = await OpenMessageAsync(client, source.FolderId, source.MessageId, ct, FolderAccess.ReadWrite);
            var request = source.Mode == ReplyMode.Forward
                ? new StoreFlagsRequest(StoreAction.Add, new HashSet<string> { "$Forwarded" }) { Silent = true }
                : new StoreFlagsRequest(StoreAction.Add, MessageFlags.Answered) { Silent = true };
            await folder.StoreAsync([uid], request, ct);
        }
        catch (Exception e) when (e is MailboxNotFoundException or ImapCommandException)
        {
            // The original was moved or deleted meanwhile, or the folder takes no keywords.
        }
    }

    private sealed record ForwardedPart(MimeEntity Entity, long Size);

    private sealed record SourceMessage(string? MessageId, IReadOnlyList<string> References, IReadOnlyList<ForwardedPart> ForwardedParts);

    private Task<SourceMessage> LoadSourceAsync(MailAccount account, SendSource source, IReadOnlyList<string> forwardPartIds, CancellationToken ct) =>
        Run(account, async (client, token) =>
        {
            var (folder, uid) = await OpenMessageAsync(client, source.FolderId, source.MessageId, token);
            var summary = await FetchOneAsync(folder, uid,
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.References | MessageSummaryItems.BodyStructure, token);

            var parts = new List<ForwardedPart>();
            foreach (var partId in forwardPartIds.Distinct())
            {
                var specifier = MailboxIds.PartSpecifier(partId);
                var part = summary.Attachments.FirstOrDefault(p => p.PartSpecifier == specifier)
                           ?? throw new MailboxValidationException("source", "An attachment of the forwarded message no longer exists.");
                var entity = await FetchPartAsync(folder, uid, part, token);
                if (entity is MimePart mime)
                {
                    mime.FileName = FileNameOf(part);
                    mime.ContentDisposition ??= new ContentDisposition(ContentDisposition.Attachment);
                    mime.ContentDisposition.FileName = FileNameOf(part);
                }

                parts.Add(new ForwardedPart(entity, part.Octets));
            }

            return new SourceMessage(
                summary.Envelope?.MessageId,
                summary.References?.ToList() ?? [],
                parts);
        }, ct);

    private static async Task<string> BodyTextAsync(IMailFolder folder, UniqueId uid, IMessageSummary summary, CancellationToken ct)
    {
        if (summary.TextBody is { } textPart && await FetchPartAsync(folder, uid, textPart, ct) is TextPart text)
        {
            return text.Text;
        }

        if (summary.HtmlBody is { } htmlPart && await FetchPartAsync(folder, uid, htmlPart, ct) is TextPart html)
        {
            return ReplyBuilder.HtmlToText(html.Text);
        }

        return "";
    }

    private static InternetAddressList ParseAddresses(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        if (!InternetAddressList.TryParse(value, out var list) || list.Count == 0)
        {
            throw new MailboxValidationException(field, $"\"{value.Trim()}\" is not a valid list of email addresses.");
        }

        var result = new InternetAddressList();
        foreach (var address in list)
        {
            // Groups ("undisclosed-recipients:;") have no deliverable address of their own.
            if (address is not MailboxAddress mailbox || !mailbox.Address.Contains('@') || mailbox.Address.EndsWith('@'))
            {
                throw new MailboxValidationException(field, $"\"{address}\" is not a valid email address.");
            }

            result.Add(mailbox);
        }

        return result;
    }
}
