namespace SocklessMail.Api.Mailbox;

/// <summary>Set or clear \Seen and \Flagged; a null value leaves that flag alone.</summary>
public sealed record FlagsInput(IReadOnlyList<string>? MessageIds, bool? Seen, bool? Flagged);

public sealed record MoveInput(IReadOnlyList<string>? MessageIds, string? TargetFolderId);

/// <summary>Moves to Trash; <see cref="Permanent"/> (or messages already in Trash) expunges instead.</summary>
public sealed record DeleteInput(IReadOnlyList<string>? MessageIds, bool Permanent);

public sealed record DeleteResult(bool MovedToTrash, string? TrashFolderId);

public sealed record CreateFolderInput(string? Name, string? ParentId);

public sealed record RenameFolderInput(string? Name);

/// <summary>Answered with 400 and the field's message.</summary>
public sealed class MailboxValidationException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}

/// <summary>
/// Answered with 409. Codes: stale_messages (the folder was renumbered, reload it),
/// no_trash (there is no Trash folder; ask before deleting permanently), folder_exists.
/// </summary>
public sealed class MailboxConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
