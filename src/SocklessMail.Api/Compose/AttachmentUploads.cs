using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SocklessMail.Api.Compose;

public sealed record UploadedAttachment(string Id, string FileName, string ContentType, long Size, DateTimeOffset UploadedAt);

/// <summary>
/// Files a user attached to a message being written, kept until it is sent (then deleted) or for
/// at most <see cref="Lifetime"/>. One directory per user, named by a hash of the user id; an
/// upload is only ever found under its owner's directory, so another user's id is "not found".
/// </summary>
public sealed partial class AttachmentUploads(SocklessMailOptions options, TimeProvider clock, ILogger<AttachmentUploads> logger)
{
    public const long MaxFileBytes = 25 * 1024 * 1024;
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex IdFormat();

    private string Root => Path.Combine(options.StorageDirectory, "uploads");

    private string UserDirectory(string userId) =>
        Path.Combine(Root, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userId))));

    /// <returns>The upload, or null when it exceeds <see cref="MaxFileBytes"/>.</returns>
    public async Task<UploadedAttachment?> SaveAsync(string userId, string fileName, string contentType, Stream content, CancellationToken ct)
    {
        var directory = UserDirectory(userId);
        Directory.CreateDirectory(directory);
        var id = Guid.NewGuid().ToString("N");
        var dataPath = Path.Combine(directory, id + ".bin");

        long size = 0;
        await using (var file = new FileStream(dataPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
            {
                size += read;
                if (size > MaxFileBytes)
                {
                    break;
                }

                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }

        if (size > MaxFileBytes)
        {
            File.Delete(dataPath);
            return null;
        }

        var upload = new UploadedAttachment(id, CleanFileName(fileName), CleanContentType(contentType), size, clock.GetUtcNow());
        await File.WriteAllTextAsync(Path.Combine(directory, id + ".json"), JsonSerializer.Serialize(upload), ct);
        return upload;
    }

    public async Task<UploadedAttachment?> GetAsync(string userId, string id, CancellationToken ct)
    {
        if (!IdFormat().IsMatch(id))
        {
            return null;
        }

        var metaPath = Path.Combine(UserDirectory(userId), id + ".json");
        if (!File.Exists(metaPath))
        {
            return null;
        }

        var upload = JsonSerializer.Deserialize<UploadedAttachment>(await File.ReadAllTextAsync(metaPath, ct));
        return upload is not null && clock.GetUtcNow() - upload.UploadedAt < Lifetime ? upload : null;
    }

    public Stream OpenRead(string userId, UploadedAttachment upload) =>
        new FileStream(Path.Combine(UserDirectory(userId), upload.Id + ".bin"), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

    public void Delete(string userId, string id)
    {
        if (!IdFormat().IsMatch(id))
        {
            return;
        }

        var directory = UserDirectory(userId);
        if (!Directory.Exists(directory))
        {
            return;
        }

        File.Delete(Path.Combine(directory, id + ".bin"));
        File.Delete(Path.Combine(directory, id + ".json"));
    }

    /// <summary>Removes uploads older than <see cref="Lifetime"/> (abandoned messages).</summary>
    public void DeleteExpired()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        var cutoff = clock.GetUtcNow().UtcDateTime - Lifetime;
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException e)
                {
                    logger.LogWarning(e, "Could not delete expired upload");
                }
            }
        }
    }

    private static string CleanFileName(string name)
    {
        var cleaned = string.Concat(Path.GetFileName(name ?? "").Where(c => !char.IsControl(c) && c is not ('/' or '\\'))).Trim();
        return cleaned.Length == 0 ? "attachment" : cleaned.Length > 255 ? cleaned[..255] : cleaned;
    }

    private static string CleanContentType(string contentType) =>
        MimeKit.ContentType.TryParse(contentType ?? "", out var parsed) ? parsed.MimeType.ToLowerInvariant() : "application/octet-stream";
}

/// <summary>Deletes abandoned uploads every hour.</summary>
public sealed class AttachmentUploadJanitor(AttachmentUploads uploads, ILogger<AttachmentUploadJanitor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                uploads.DeleteExpired();
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Cleaning up expired uploads failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
