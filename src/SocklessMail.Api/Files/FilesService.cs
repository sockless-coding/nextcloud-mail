using SocklessMail.Api.Accounts;
using SocklessMail.Api.Compose;
using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Files;

/// <summary>Moves files between the user's Nextcloud Files and their mail: attaching, linking and saving attachments.</summary>
public sealed class FilesService(NextcloudFiles files, AttachmentUploads uploads, MailboxService mailbox)
{
    /// <summary>At most this many files per request, as for a message's attachments.</summary>
    public const int MaxFiles = MailboxService.MaxAttachments;

    /// <summary>
    /// Copies files from Files into the user's attachment uploads. Each file succeeds or fails on
    /// its own; failures say why (gone, a folder, too large).
    /// </summary>
    public async Task<IReadOnlyList<AttachedFile>> AttachAsync(string userId, AttachFromFilesInput input, CancellationToken ct)
    {
        var paths = (input.Paths ?? []).ToList();
        var fileIds = (input.FileIds ?? []).Distinct().ToList();
        if (paths.Count + fileIds.Count is 0 or > MaxFiles)
        {
            throw new MailboxValidationException("paths", $"Choose between 1 and {MaxFiles} files.");
        }

        var results = new List<AttachedFile>();
        if (fileIds.Count > 0)
        {
            var found = await files.FindByIdsAsync(userId, fileIds, ct);
            foreach (var id in fileIds)
            {
                var entry = found.FirstOrDefault(f => f.FileId == id).Entry;
                if (entry is null)
                {
                    results.Add(Failed($"#{id}", null, NextcloudFilesException.NotFound()));
                }
                else if (entry.IsFolder)
                {
                    results.Add(Failed(entry.Name, entry.Path, IsFolder()));
                }
                else
                {
                    paths.Add(entry.Path);
                }
            }
        }

        foreach (var path in paths)
        {
            var name = NextcloudFiles.SplitPath(path) is { Length: > 0 } names ? names[^1] : path;
            try
            {
                var upload = await files.ReadAsync(userId, path, async (fileName, contentType, length, content) =>
                    length > AttachmentUploads.MaxFileBytes
                        ? null
                        : await uploads.SaveAsync(userId, fileName, contentType, content, ct), ct);
                results.Add(upload is null
                    ? Failed(name, path, new NextcloudFilesException("too_large",
                        $"Larger than {AttachmentUploads.MaxFileBytes / (1024 * 1024)} MB; insert a share link instead."))
                    : new AttachedFile(upload.FileName, path, upload, null, null));
            }
            catch (NextcloudFilesException e) when (e.Code is not "nextcloud_unavailable")
            {
                results.Add(Failed(name, path, e));
            }
        }

        return results;
    }

    public async Task<ShareLink> CreateShareLinkAsync(string userId, ShareLinkInput input, CancellationToken ct)
    {
        var names = NextcloudFiles.SplitPath(input.Path) is { Length: > 0 } n
            ? n
            : throw new MailboxValidationException("path", "Choose a file or folder.");
        var (url, token) = await files.CreateShareLinkAsync(userId, NextcloudFiles.JoinPath(names), ct);
        return new ShareLink(names[^1], url, token);
    }

    /// <summary>
    /// Saves attachments of a message into a folder of Files, never replacing existing files.
    /// A problem with the folder itself (gone, read-only, quota) fails the whole request; a
    /// missing attachment fails only that one.
    /// </summary>
    public async Task<IReadOnlyList<SavedPart>> SaveAttachmentsAsync(
        string userId, MailAccount account, string folderId, string messageId, SaveToFilesInput input, CancellationToken ct)
    {
        var folder = NextcloudFiles.SplitPath(input.Folder) ?? throw new MailboxValidationException("folder", "Choose a folder.");
        var partIds = (input.PartIds ?? []).Distinct().ToList();
        if (partIds.Count is 0 or > MaxFiles)
        {
            throw new MailboxValidationException("partIds", $"Choose between 1 and {MaxFiles} attachments.");
        }

        var saved = new List<SavedPart>();
        foreach (var partId in partIds)
        {
            PartContent part;
            try
            {
                part = await mailbox.GetPartAsync(account, folderId, messageId, partId, ct);
            }
            catch (MailboxNotFoundException)
            {
                saved.Add(new SavedPart(partId, null, "not_found", "The attachment no longer exists."));
                continue;
            }

            // Buffered on disk: the upload is sent again when its name was taken in the meantime.
            await using var buffer = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite,
                FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
            await part.WriteToAsync(buffer, ct);
            var path = await files.UploadAsync(userId, NextcloudFiles.JoinPath(folder), part.FileName, part.ContentType, buffer, ct);
            saved.Add(new SavedPart(partId, path, null, null));
        }

        return saved;
    }

    private static NextcloudFilesException IsFolder() => new("is_folder", "Folders cannot be attached; insert a share link instead.");

    private static AttachedFile Failed(string name, string? path, NextcloudFilesException e) => new(name, path, null, e.Code, e.Message);
}
