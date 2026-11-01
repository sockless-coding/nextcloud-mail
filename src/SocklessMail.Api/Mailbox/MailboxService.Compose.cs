using MailKit;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Search;
using MimeKit;
using MimeKit.Utils;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Compose;
using SocklessMail.Api.Mail;
using SocklessMail.Api.Sync;

namespace SocklessMail.Api.Mailbox;

/// <summary>Writing mail: reply/forward drafts, saved drafts, and sending with a copy in Sent.</summary>
public sealed partial class MailboxService
{
    public const int MaxRecipients = 100;
    public const int MaxAttachments = 25;
    public const int MaxInlineImages = 100;
    public const long MaxAttachmentBytes = 25 * 1024 * 1024;
    public const int MaxTextLength = 1024 * 1024;

    /// <summary>The fields a message is built from, sent or saved as a draft.</summary>
    private sealed record MessageFields(
        string? To, string? Cc, string? Bcc, string? Subject, string? Text, string? Html, IReadOnlyList<string>? AttachmentIds, SendSource? Source);

    private sealed record ComposedMessage(MimeMessage Message, IReadOnlyList<UploadedAttachment> Uploads, int Recipients);

    public Task<DraftDto> GetDraftAsync(MailAccount account, string folderId, string messageId, ReplyMode mode, CancellationToken ct) =>
        Run(account, async (client, token) =>
        {
            var (folder, uid) = await OpenMessageAsync(client, folderId, messageId, token);
            var summary = await FetchOneAsync(folder, uid, FolderCache.ListItems & ~MessageSummaryItems.PreviewText, token);
            var envelope = summary.Envelope ?? new Envelope();
            var (text, html) = await BodiesAsync(folder, uid, summary, token);
            var attachments = summary.Attachments.Select(ToAttachmentDto).ToArray();

            // The images the HTML shows from the message's own parts; the editor shows them, and
            // sending takes them from this message again (see LoadSourceAsync).
            var parts = InlinePartsOf(summary);
            var images = OutgoingHtml.ContentIds(OutgoingHtml.SanitizeWithImages(html))
                .Where(cid => parts.TryGetValue(cid, out var part) && OutgoingHtml.InlineImageTypes.Contains(part.ContentType.MimeType))
                .Select(cid => new InlineImageDto(cid, MailboxIds.PartId(parts[cid].PartSpecifier)))
                .ToArray();
            var shown = images.Select(i => i.ContentId).ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (mode == ReplyMode.Edit)
            {
                return new DraftDto(
                    envelope.To?.ToString() ?? "",
                    envelope.Cc?.ToString() ?? "",
                    envelope.Bcc?.ToString() ?? "",
                    envelope.Subject ?? "",
                    text,
                    OutgoingHtml.KeepImages(OutgoingHtml.SanitizeWithImages(html), shown),
                    attachments,
                    new DraftSource(folderId, messageId, mode),
                    images);
            }

            var (to, cc) = ReplyBuilder.Recipients(mode, envelope.From, envelope.ReplyTo, envelope.To, envelope.Cc, account.EmailAddress);
            var date = MessageDate(summary);
            return new DraftDto(
                to.ToString(),
                cc.ToString(),
                "",
                ReplyBuilder.Subject(mode, envelope.Subject),
                ReplyBuilder.QuotedText(mode, date, envelope.From, envelope.To, envelope.Cc, envelope.Subject, text),
                OutgoingHtml.Quote(mode, date, envelope.From, envelope.To, envelope.Cc, envelope.Subject, html, shown),
                mode == ReplyMode.Forward ? attachments : [],
                new DraftSource(folderId, messageId, mode),
                images);
        }, ct);

    /// <summary>A message's parts with a Content-ID (by it, without brackets): what its HTML can show inline.</summary>
    private static Dictionary<string, BodyPartBasic> InlinePartsOf(IMessageSummary summary) =>
        LeafParts(summary.Body)
            .Where(p => !string.IsNullOrEmpty(p.ContentId))
            .GroupBy(p => p.ContentId!.Trim().Trim('<', '>'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    public async Task<SendResult> SendAsync(MailAccount account, SendInput input, CancellationToken ct)
    {
        var composed = await ComposeAsync(account, FieldsOf(input), draft: false, ct);

        var credential = await logins.ForConnectionAsync(account, ct);
        await DeliverAsync(account, credential, composed.Message, ct);
        logger.LogInformation("Sent message from account {AccountId} to {RecipientCount} recipients", account.Id, composed.Recipients);

        DeleteUploads(account, composed);
        var result = await AfterSendAsync(account, composed.Message, input.Source, ct);
        await RemoveDraftsAsync(account, input);
        return result;
    }

    /// <summary>
    /// Builds the message to be sent later and hands it to <paramref name="store"/>. Once stored,
    /// its uploads and saved draft go, as after sending: everything it needs is inside it now.
    /// </summary>
    public async Task ScheduleAsync(MailAccount account, SendInput input, Func<MimeMessage, CancellationToken, Task> store, CancellationToken ct)
    {
        var composed = await ComposeAsync(account, FieldsOf(input), draft: false, ct);
        await store(composed.Message, ct);
        DeleteUploads(account, composed);
        await RemoveDraftsAsync(account, input);
    }

    /// <summary>
    /// Sends a message built earlier (see <see cref="ScheduleAsync"/>), dated now, with a copy in
    /// Sent and the original marked. <paramref name="delivering"/> is called once the SMTP
    /// connection stands: from then on a failure may mean the message went out anyway.
    /// </summary>
    public async Task<SendResult> SendStoredAsync(MailAccount account, MimeMessage message, SendSource? source, Action delivering, CancellationToken ct)
    {
        message.Date = clock.GetUtcNow();
        await DeliverAsync(account, await logins.ForConnectionAsync(account, ct), message, ct, delivering);
        logger.LogInformation("Sent a scheduled message from account {AccountId}", account.Id);
        return await AfterSendAsync(account, message, source, ct);
    }

    /// <summary>Puts a message built earlier back into Drafts (flagged \Draft and \Seen), to be continued there.</summary>
    public Task<SavedDraftRef?> RestoreDraftAsync(MailAccount account, MimeMessage message, CancellationToken ct) =>
        Run(account, async (client, token) =>
        {
            var drafts = await FindSpecialFolderAsync(client, "drafts", token) ?? await CreateDraftsFolderAsync(client, token);
            var uid = await drafts.AppendAsync(message, MessageFlags.Seen | MessageFlags.Draft, token);
            if (uid is null && message.MessageId is { } header)
            {
                await drafts.OpenAsync(FolderAccess.ReadOnly, token);
                uid = (await drafts.SearchAsync(SearchQuery.HeaderContains("Message-ID", header), token)).Cast<UniqueId?>().LastOrDefault();
            }

            return uid is { } u ? new SavedDraftRef(MailboxIds.FolderId(drafts.FullName), MailboxIds.MessageId(u.Validity, u.Id)) : null;
        }, ct, retry: false);

    private static MessageFields FieldsOf(SendInput input) =>
        new(input.To, input.Cc, input.Bcc, input.Subject, input.Text, input.Html, input.AttachmentIds, input.Source);

    private void DeleteUploads(MailAccount account, ComposedMessage composed)
    {
        foreach (var upload in composed.Uploads)
        {
            uploads.Delete(account.OwnerUserId, upload.Id);
        }
    }

    /// <summary>The draft has served its purpose; so has a continued draft the message was built from.</summary>
    private async Task RemoveDraftsAsync(MailAccount account, SendInput input)
    {
        if (input.Draft is { } draft)
        {
            await RemoveDraftAsync(account, draft);
        }

        if (input.Source is { Mode: ReplyMode.Edit } source && (input.Draft is null || input.Draft.MessageId != source.MessageId))
        {
            await RemoveDraftAsync(account, new SavedDraftRef(source.FolderId, source.MessageId));
        }
    }

    /// <summary>
    /// Sends a message the app built itself (an invitation reply) from the account, with a copy
    /// in Sent like any other message.
    /// </summary>
    public async Task<SendResult> SendGeneratedAsync(MailAccount account, MimeMessage message, CancellationToken ct)
    {
        message.Date = clock.GetUtcNow();
        message.MessageId ??= MimeUtils.GenerateMessageId(account.EmailAddress.Split('@')[^1]);
        await DeliverAsync(account, await logins.ForConnectionAsync(account, ct), message, ct);
        logger.LogInformation("Sent a generated message from account {AccountId}", account.Id);
        return await AfterSendAsync(account, message, null, ct);
    }

    /// <summary>
    /// Stores the draft in the account's Drafts folder (created when there is none), flagged
    /// \Draft and \Seen, then removes the version it replaces. A continued draft (source mode
    /// Edit) takes its attachments from the version it replaces, so the new copy becomes its
    /// source, and the uploads now inside it are dropped.
    /// </summary>
    public async Task<SaveDraftResult> SaveDraftAsync(MailAccount account, SaveDraftInput input, CancellationToken ct)
    {
        var composed = await ComposeAsync(account,
            new MessageFields(input.To, input.Cc, input.Bcc, input.Subject, input.Text, input.Html, input.AttachmentIds, input.Source),
            draft: true, ct);
        var editing = input.Source is { Mode: ReplyMode.Edit };

        var (fullName, saved, attachments) = await Run(account, async (client, token) =>
        {
            var drafts = await FindSpecialFolderAsync(client, "drafts", token) ?? await CreateDraftsFolderAsync(client, token);
            var uid = await drafts.AppendAsync(composed.Message, MessageFlags.Seen | MessageFlags.Draft, token);
            if (uid is null || editing)
            {
                await drafts.OpenAsync(FolderAccess.ReadOnly, token);
            }

            // Without UIDPLUS the server does not say where the message went.
            uid ??= (await drafts.SearchAsync(SearchQuery.HeaderContains("Message-ID", composed.Message.MessageId!), token)).Cast<UniqueId?>().LastOrDefault();

            AttachmentDto[]? parts = null;
            if (editing && uid is { } id)
            {
                var summary = await FetchOneAsync(drafts, id, MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure, token);
                parts = summary.Attachments.Select(ToAttachmentDto).ToArray();
            }

            return (drafts.FullName, uid, parts);
        }, ct, retry: false);

        var folderId = MailboxIds.FolderId(fullName);
        var messageId = saved is { } u ? MailboxIds.MessageId(u.Validity, u.Id) : null;

        // A continued draft whose new copy cannot be found keeps its old one: it is still the source.
        if (input.Replaces is { } old && old.MessageId != messageId && (!editing || messageId is not null))
        {
            await RemoveDraftAsync(account, old);
        }

        if (editing && messageId is not null)
        {
            foreach (var upload in composed.Uploads)
            {
                uploads.Delete(account.OwnerUserId, upload.Id);
            }

            return new SaveDraftResult(folderId, messageId, new DraftSource(folderId, messageId, ReplyMode.Edit), attachments);
        }

        return new SaveDraftResult(folderId, messageId, null, null);
    }

    /// <summary>Best effort: a draft left behind is a nuisance, not a failure of what the user did.</summary>
    private async Task RemoveDraftAsync(MailAccount account, SavedDraftRef draft)
    {
        try
        {
            await DeleteAsync(account, draft.FolderId, new DeleteInput([draft.MessageId], Permanent: true), CancellationToken.None);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("Removing a saved draft of account {AccountId} failed ({ExceptionType})", account.Id, e.GetType().Name);
        }
    }

    private static async Task<IMailFolder> CreateDraftsFolderAsync(ImapClient client, CancellationToken ct)
    {
        var root = client.GetFolder(client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', ""));
        var drafts = await root.CreateAsync("Drafts", isMessageFolder: true, ct)
                     ?? throw new MailboxConflictException("folder_exists", "A Drafts folder could not be created.");
        await drafts.SubscribeAsync(ct);
        return drafts;
    }

    /// <summary>
    /// Builds the message. A draft may be incomplete: no recipients, or addresses still being
    /// typed (kept as written). Everything else is checked the same way for both.
    /// </summary>
    private async Task<ComposedMessage> ComposeAsync(MailAccount account, MessageFields input, bool draft, CancellationToken ct)
    {
        InternetAddressList? Addresses(string? value, string field)
        {
            try
            {
                return ParseAddresses(value, field);
            }
            catch (MailboxValidationException) when (draft)
            {
                return null;
            }
        }

        var to = Addresses(input.To, "to");
        var cc = Addresses(input.Cc, "cc");
        var bcc = Addresses(input.Bcc, "bcc");
        var recipients = (to?.Count ?? 0) + (cc?.Count ?? 0) + (bcc?.Count ?? 0);
        if (recipients == 0 && !draft)
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
        var html = string.IsNullOrWhiteSpace(input.Html) ? null : input.Html;
        if (text.Length > MaxTextLength || html?.Length > MaxTextLength)
        {
            throw new MailboxValidationException("text", "The message is too long.");
        }

        var fragment = html is null ? null : OutgoingHtml.SanitizeWithImages(html);
        var contentIds = fragment is null ? [] : OutgoingHtml.ContentIds(fragment);
        var attachmentIds = input.AttachmentIds ?? [];
        var forwardPartIds = input.Source is { Mode: ReplyMode.Forward or ReplyMode.Edit } ? input.Source.ForwardPartIds ?? [] : [];
        if (attachmentIds.Count + forwardPartIds.Count > MaxAttachments)
        {
            throw new MailboxValidationException("attachmentIds", $"At most {MaxAttachments} attachments per message.");
        }

        // More than attachments: a quoted newsletter brings its own.
        if (contentIds.Count > MaxInlineImages)
        {
            throw new MailboxValidationException("text", $"At most {MaxInlineImages} images per message.");
        }

        var uploaded = new List<UploadedAttachment>();
        foreach (var id in attachmentIds.Distinct())
        {
            uploaded.Add(await uploads.GetAsync(account.OwnerUserId, id, ct)
                ?? throw new MailboxValidationException("attachmentIds", "An attachment has expired or was removed. Attach it again."));
        }

        // Images of the source: a continued draft's own (also those inserted before it was saved),
        // a quoted original's.
        var original = input.Source is { } source ? await LoadSourceAsync(account, source, forwardPartIds, contentIds, ct) : null;
        var inlineUploads = new List<(string ContentId, UploadedAttachment Upload)>();
        foreach (var contentId in contentIds.Where(cid => original?.InlineParts.ContainsKey(cid) != true))
        {
            var upload = OutgoingHtml.UploadIdOf(contentId) is { } uploadId ? await uploads.GetAsync(account.OwnerUserId, uploadId, ct) : null;
            if (upload is not null && !OutgoingHtml.InlineImageTypes.Contains(upload.ContentType))
            {
                throw new MailboxValidationException("text", $"\"{upload.FileName}\" cannot be shown in the message. Attach it instead.");
            }

            if (upload is not null)
            {
                inlineUploads.Add((contentId, upload));
            }
            else if (!draft)
            {
                // A draft is saved without it; sending would lose it unnoticed.
                throw new MailboxValidationException("text", "An image in the message has expired or was removed. Insert it again.");
            }
        }

        if (fragment is not null)
        {
            var available = inlineUploads.Select(i => i.ContentId).Concat(original?.InlineParts.Keys ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
            fragment = OutgoingHtml.KeepImages(fragment, available);
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(account.SenderName, account.EmailAddress));
        SetAddresses(message, HeaderId.To, message.To, to, input.To);
        SetAddresses(message, HeaderId.Cc, message.Cc, cc, input.Cc);
        SetAddresses(message, HeaderId.Bcc, message.Bcc, bcc, input.Bcc);
        message.Subject = subject;
        message.Date = clock.GetUtcNow();
        message.MessageId = MimeUtils.GenerateMessageId(account.EmailAddress.Split('@')[^1]);
        if (original is not null)
        {
            switch (input.Source!.Mode)
            {
                case ReplyMode.Reply or ReplyMode.ReplyAll when original.MessageId is { } inReplyTo:
                    // Threading: the replied-to message and its own ancestors.
                    message.InReplyTo = inReplyTo;
                    message.References.AddRange(original.References);
                    message.References.Add(inReplyTo);
                    break;
                case ReplyMode.Edit:
                    // A continued draft keeps the threading it was saved with.
                    message.InReplyTo = original.InReplyTo;
                    message.References.AddRange(original.References);
                    break;
            }
        }

        var builder = new BodyBuilder();
        if (fragment is not null)
        {
            builder.HtmlBody = OutgoingHtml.Document(fragment);
            builder.TextBody = ReplyBuilder.HtmlToText(fragment);
        }
        else
        {
            builder.TextBody = text;
        }

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

            // Images in the HTML: parts of a multipart/related, by the Content-ID the HTML refers to.
            if (fragment is not null)
            {
                foreach (var (contentId, upload) in inlineUploads)
                {
                    var stream = uploads.OpenRead(account.OwnerUserId, upload);
                    streams.Add(stream);
                    total += upload.Size;
                    var image = builder.LinkedResources.Add(upload.FileName, stream, ContentType.Parse(upload.ContentType));
                    image.ContentId = contentId;
                    image.ContentDisposition = new ContentDisposition(ContentDisposition.Inline) { FileName = upload.FileName };
                }

                foreach (var part in original?.InlineParts.Values ?? [])
                {
                    total += part.Size;
                    builder.LinkedResources.Add(part.Entity);
                }
            }

            if (total > MaxAttachmentBytes)
            {
                throw new MailboxValidationException("attachmentIds", $"Attachments and images may total at most {MaxAttachmentBytes / (1024 * 1024)} MB.");
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

        return new ComposedMessage(message, [.. uploaded, .. inlineUploads.Select(i => i.Upload)], recipients);
    }

    /// <summary>Parsed addresses, or for a draft whose field does not parse yet, the text as written.</summary>
    private static void SetAddresses(MimeMessage message, HeaderId header, InternetAddressList target, InternetAddressList? parsed, string? raw)
    {
        if (parsed is not null)
        {
            target.AddRange(parsed);
        }
        else if (!string.IsNullOrWhiteSpace(raw))
        {
            message.Headers.Replace(header, raw.ReplaceLineEndings(" ").Trim());
        }
    }

    private async Task DeliverAsync(MailAccount account, MailCredential credential, MimeMessage message, CancellationToken ct, Action? delivering = null)
    {
        var endpoint = new MailEndpoint(account.SmtpHost, account.SmtpPort, account.SmtpSecurity);
        using var smtp = await connector.ConnectSmtpAsync(endpoint, account.Username, credential, ct);
        delivering?.Invoke();
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

                // A continued draft is no original: it is removed instead (see SendAsync).
                if (source is { Mode: not ReplyMode.Edit })
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

    /// <param name="InlineParts">The source's parts the new HTML shows, by Content-ID.</param>
    private sealed record SourceMessage(
        string? MessageId, string? InReplyTo, IReadOnlyList<string> References, IReadOnlyList<ForwardedPart> ForwardedParts,
        IReadOnlyDictionary<string, ForwardedPart> InlineParts);

    private Task<SourceMessage> LoadSourceAsync(
        MailAccount account, SendSource source, IReadOnlyList<string> forwardPartIds, IReadOnlyList<string> inlineContentIds, CancellationToken ct) =>
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

            var inline = new Dictionary<string, ForwardedPart>(StringComparer.OrdinalIgnoreCase);
            var withContentId = InlinePartsOf(summary);
            foreach (var contentId in inlineContentIds)
            {
                if (withContentId.TryGetValue(contentId, out var part)
                    && OutgoingHtml.InlineImageTypes.Contains(part.ContentType.MimeType)
                    && await FetchPartAsync(folder, uid, part, token) is MimePart image)
                {
                    image.ContentId = contentId;
                    // As the server reported it (some in upper case); as the upload had it.
                    image.ContentType.MediaType = image.ContentType.MediaType.ToLowerInvariant();
                    image.ContentType.MediaSubtype = image.ContentType.MediaSubtype.ToLowerInvariant();
                    image.ContentDisposition ??= new ContentDisposition(ContentDisposition.Inline);
                    inline[contentId] = new ForwardedPart(image, part.Octets);
                }
            }

            return new SourceMessage(
                summary.Envelope?.MessageId,
                summary.Envelope?.InReplyTo,
                summary.References?.ToList() ?? [],
                parts,
                inline);
        }, ct);

    /// <summary>What a message says, as plain text, with who sent it and when (for a task made from it).</summary>
    public Task<MessageText> GetMessageTextAsync(MailAccount account, string folderId, string messageId, CancellationToken ct) =>
        Run(account, async (client, token) =>
        {
            var (folder, uid) = await OpenMessageAsync(client, folderId, messageId, token);
            var summary = await FetchOneAsync(folder, uid, FolderCache.ListItems & ~MessageSummaryItems.PreviewText, token);
            var envelope = summary.Envelope ?? new Envelope();
            var (text, _) = await BodiesAsync(folder, uid, summary, token);
            return new MessageText(envelope.Subject ?? "", envelope.From.Mailboxes.FirstOrDefault(), MessageDate(summary), text, envelope.MessageId);
        }, ct);

    /// <summary>The message's text and HTML; each made from the other when the message has only one.</summary>
    private static async Task<(string Text, string Html)> BodiesAsync(IMailFolder folder, UniqueId uid, IMessageSummary summary, CancellationToken ct)
    {
        var text = summary.TextBody is { } textPart && await FetchPartAsync(folder, uid, textPart, ct) is TextPart plain ? plain.Text : null;
        var html = summary.HtmlBody is { } htmlPart && await FetchPartAsync(folder, uid, htmlPart, ct) is TextPart rich ? rich.Text : null;
        return (text ?? (html is null ? "" : ReplyBuilder.HtmlToText(html)), html ?? OutgoingHtml.FromText(text ?? ""));
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
