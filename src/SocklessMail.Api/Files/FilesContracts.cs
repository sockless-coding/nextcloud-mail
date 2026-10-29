using SocklessMail.Api.Compose;

namespace SocklessMail.Api.Files;

/// <summary>Files to attach, by path (the file picker) or by file id (the Files app's "Send by email").</summary>
public sealed record AttachFromFilesInput(string[]? Paths, long[]? FileIds);

/// <summary>One file of an attach request: its upload, or why it was not attached.</summary>
public sealed record AttachedFile(string Name, string? Path, UploadedAttachment? Upload, string? Error, string? Message);

public sealed record ShareLinkInput(string? Path);

/// <summary>
/// <paramref name="Url"/> uses the host this app reaches Nextcloud by, which may be an internal
/// one; the browser builds the link from <paramref name="Token"/> and its own address instead.
/// </summary>
public sealed record ShareLink(string Name, string Url, string Token);

/// <summary>Attachments of one message to save into a folder of Files.</summary>
public sealed record SaveToFilesInput(string? Folder, string[]? PartIds);

/// <summary>Where one attachment was saved, or why it was not.</summary>
public sealed record SavedPart(string PartId, string? Path, string? Error, string? Message);
