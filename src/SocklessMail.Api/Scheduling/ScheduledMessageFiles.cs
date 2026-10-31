using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using MimeKit;

namespace SocklessMail.Api.Scheduling;

/// <summary>
/// The messages waiting to be sent, one encrypted file each under the persistent storage, with
/// the same key ring as the account credentials. Bound to the scheduled message's id: a file
/// copied over another one does not decrypt.
/// </summary>
public sealed class ScheduledMessageFiles(SocklessMailOptions options, IDataProtectionProvider protection, ILogger<ScheduledMessageFiles> logger)
{
    private string Root => Path.Combine(options.StorageDirectory, "scheduled");

    private string PathOf(Guid id) => Path.Combine(Root, id.ToString("N") + ".bin");

    private IDataProtector For(Guid id) => protection.CreateProtector("SocklessMail.Scheduled.v1", id.ToString("N"));

    /// <returns>The size of the message (not of the encrypted file).</returns>
    public async Task<long> SaveAsync(Guid id, MimeMessage message, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        using var plain = new MemoryStream();
        await message.WriteToAsync(plain, ct);
        var path = PathOf(id);
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, For(id).Protect(plain.ToArray()), ct);
        File.Move(temporary, path, overwrite: true);
        return plain.Length;
    }

    /// <returns>The message, or null when its file is gone or no longer decrypts.</returns>
    public async Task<MimeMessage?> LoadAsync(Guid id, CancellationToken ct)
    {
        byte[] plain;
        try
        {
            plain = For(id).Unprotect(await File.ReadAllBytesAsync(PathOf(id), ct));
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or CryptographicException)
        {
            logger.LogWarning("The stored scheduled message {ScheduledId} cannot be read ({ExceptionType})", id, e.GetType().Name);
            return null;
        }

        using var stream = new MemoryStream(plain, writable: false);
        return await MimeMessage.LoadAsync(stream, ct);
    }

    public void Delete(Guid id)
    {
        try
        {
            File.Delete(PathOf(id));
        }
        catch (IOException e)
        {
            logger.LogWarning(e, "Could not delete the stored scheduled message {ScheduledId}", id);
        }
    }

    /// <summary>Removes files without a row (their account was deleted, or a crash between the two writes).</summary>
    public void DeleteOrphans(IReadOnlySet<Guid> known)
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(Root))
        {
            var name = Path.GetFileName(file);
            var stem = name.Split('.')[0];
            // A file being written right now is no orphan.
            if (Guid.TryParseExact(stem, "N", out var id) && known.Contains(id)
                || File.GetLastWriteTimeUtc(file) > DateTime.UtcNow.AddMinutes(-10))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (IOException e)
            {
                logger.LogWarning(e, "Could not delete an orphaned scheduled message");
            }
        }
    }
}
