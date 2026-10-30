using System.Security.Claims;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.AppApi;
using SocklessMail.Api.Mail;
using SocklessMail.Api.OAuth;
using SocklessMail.Api.Rules;
using SocklessMail.Api.Sync;

namespace SocklessMail.Api.Api;

/// <summary>An account's mail rules, which the app runs on new INBOX mail (<see cref="RuleEngine"/>).</summary>
public static class RuleEndpoints
{
    public static void MapRuleEndpoints(this RouteGroupBuilder api)
    {
        var rules = api.MapGroup("/accounts/{accountId:guid}/rules")
            .AddEndpointFilter<RuleErrorFilter>()
            .AddEndpointFilter<MailboxEndpoints.MailErrorFilter>();

        rules.MapGet("", async (Guid accountId, ClaimsPrincipal user, AccountService accounts, RuleStore store, CancellationToken ct) =>
            await accounts.GetAsync(user.GetNextcloudUserId(), accountId, ct) is { } account
                ? Results.Ok(await store.GetAsync(account, ct))
                : MailboxEndpoints.NotFound());

        rules.MapPut("", async (
            Guid accountId, RuleListInput input, ClaimsPrincipal user, AccountService accounts, RuleStore store,
            RuleEngine engine, ImapConnections imap, MailLogins logins, SyncLocks syncLocks, ILogger<RuleStore> logger,
            CancellationToken ct) =>
        {
            if (await accounts.GetAsync(user.GetNextcloudUserId(), accountId, ct) is not { } account)
            {
                return MailboxEndpoints.NotFound();
            }

            var (saved, needsCursor) = await store.SaveAsync(account, input.Rules, ct);
            if (needsCursor)
            {
                // Rules were just enabled: they apply to mail from now on. Positioned here rather
                // than by the next run, so a message that arrives meanwhile is not skipped. When the
                // server cannot be reached, the next run positions it instead.
                try
                {
                    using var _ = await syncLocks.AcquireAsync(account.Id, "INBOX", ct);
                    await imap.RunAsync(account, await logins.ForConnectionAsync(account, ct), async (client, token) =>
                    {
                        await engine.PositionAsync(client, account, token);
                        return true;
                    }, ct);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    logger.LogInformation("Could not position the rules of account {AccountId} yet ({ExceptionType})", account.Id, e.GetType().Name);
                }
            }

            return Results.Ok(saved);
        });

        rules.MapPost("/test", async (
            Guid accountId, RuleTestInput input, ClaimsPrincipal user, AccountService accounts,
            ImapConnections imap, MailLogins logins, CancellationToken ct) =>
        {
            if (await accounts.GetAsync(user.GetNextcloudUserId(), accountId, ct) is not { } account)
            {
                return MailboxEndpoints.NotFound();
            }

            var rule = RuleStore.Draft(account, input.Rule);
            return Results.Ok(await imap.RunAsync(account, await logins.ForConnectionAsync(account, ct),
                (client, token) => RuleEngine.TestAsync(client, rule, token), ct));
        });
    }

    /// <summary>"Run on existing messages": what a saved rule would match in a folder, then running it on those.</summary>
    public static void MapRuleRunEndpoints(this RouteGroupBuilder api)
    {
        var rule = api.MapGroup("/accounts/{accountId:guid}/rules/{ruleId:guid}")
            .AddEndpointFilter<MailboxEndpoints.MailErrorFilter>();

        rule.MapPost("/preview", async (
            Guid accountId, Guid ruleId, RuleRunInput input, ClaimsPrincipal user, AccountService accounts, RuleEngine engine, CancellationToken ct) =>
            await accounts.GetAsync(user.GetNextcloudUserId(), accountId, ct) is { } account
                ? Results.Ok(await engine.PreviewExistingAsync(account, ruleId, input.FolderId, ct))
                : MailboxEndpoints.NotFound());

        rule.MapPost("/run", async (
            Guid accountId, Guid ruleId, RuleRunInput input, ClaimsPrincipal user, AccountService accounts, RuleEngine engine, CancellationToken ct) =>
            await accounts.GetAsync(user.GetNextcloudUserId(), accountId, ct) is { } account
                ? Results.Ok(new RuleRunResult(await engine.RunOnExistingAsync(account, ruleId, input.FolderId, input.MessageIds, ct)))
                : MailboxEndpoints.NotFound());
    }

    internal sealed class RuleErrorFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            try
            {
                return await next(context);
            }
            catch (RuleValidationException e)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [e.Field] = [e.Message] });
            }
        }
    }
}
