using SocklessMail.Api.AppApi;

namespace SocklessMail.Api.Scheduling;

/// <summary>
/// Tells a user in Nextcloud's notifications that a scheduled message could not be sent: they
/// may not have the app open when it happens. Through AppAPI (POST apps/app_api/api/v1/notification),
/// which answers 404 when the notifications app is not enabled. Best effort.
/// </summary>
public sealed class ScheduledFailureNotices(NextcloudClient nextcloud, ILogger<ScheduledFailureNotices> logger)
{
    public async Task NotifyAsync(ScheduledMessage message, CancellationToken ct)
    {
        var subject = string.IsNullOrWhiteSpace(message.Subject) ? "(no subject)" : message.Subject;
        var to = message.Recipients.FirstOrDefault() is { } first
            ? message.Recipients.Length > 1 ? $"{first.Address} and {message.Recipients.Length - 1} more" : first.Address
            : "";
        var text = to.Length > 0 ? $"\"{subject}\" to {to}: {message.Error}" : $"\"{subject}\": {message.Error}";
        try
        {
            await nextcloud.OcsAsync(HttpMethod.Post, "apps/app_api/api/v1/notification", new
            {
                @params = new
                {
                    @object = "scheduled_message",
                    object_id = message.Id.ToString("N"),
                    subject_type = "scheduled_failed",
                    subject_params = new
                    {
                        // Run through Nextcloud's rich text: braces would read as placeholders.
                        rich_subject = "A scheduled message could not be sent",
                        rich_subject_params = new { },
                        rich_message = Plain(text),
                        rich_message_params = new { },
                    },
                },
            }, message.OwnerUserId, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogInformation("Could not notify about a failed scheduled message ({ExceptionType})", e.GetType().Name);
        }
    }

    private static string Plain(string text)
    {
        var cleaned = text.Replace('{', '(').Replace('}', ')').ReplaceLineEndings(" ");
        return cleaned.Length > 1000 ? cleaned[..1000] + "…" : cleaned;
    }
}
