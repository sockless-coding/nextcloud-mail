using System.Security.Claims;
using SocklessMail.Api.AppApi;
using SocklessMail.Api.Senders;

namespace SocklessMail.Api.Api;

public sealed record SenderChecksInput(IReadOnlyList<SenderChecks.Sender>? Senders);

public sealed record TrustInput(string? Domain);

/// <summary>Suspicious-sender warnings for the message list, and the domains the user trusts.</summary>
public static class SenderEndpoints
{
    public static void MapSenderEndpoints(this RouteGroupBuilder api)
    {
        // POST: a list page has up to MaxSenders senders, too many for a query string.
        api.MapPost("/sender-checks", async (SenderChecksInput input, ClaimsPrincipal user, SenderChecks checks, CancellationToken ct) =>
        {
            var senders = input.Senders ?? [];
            if (senders.Count > SenderChecks.MaxSenders)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["senders"] = [$"At most {SenderChecks.MaxSenders} senders."] });
            }

            var result = await checks.CheckAsync(user.GetNextcloudUserId(), senders, ct);
            return Results.Ok(new { warnings = result.Warnings, pending = result.Pending });
        });

        var trusted = api.MapGroup("/trusted-senders");
        trusted.MapGet("", async (SenderChecks checks, CancellationToken ct) => Results.Ok(await checks.ListTrustedAsync(ct)));

        trusted.MapPost("", async (TrustInput input, ClaimsPrincipal user, SenderChecks checks, CancellationToken ct) =>
            await checks.TrustAsync(user.GetNextcloudUserId(), input.Domain ?? "", ct) is { } domain
                ? Results.Ok(new { domain })
                : Results.ValidationProblem(new Dictionary<string, string[]> { ["domain"] = ["Enter a domain name."] }));

        trusted.MapDelete("/{domain}", async (string domain, ClaimsPrincipal user, SenderChecks checks, CancellationToken ct) =>
            await checks.DistrustAsync(user.GetNextcloudUserId(), domain, ct) ? Results.NoContent() : MailboxEndpoints.NotFound());
    }
}
