using System.Security.Claims;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.AppApi;

namespace SocklessMail.Api.Api;

/// <summary>
/// /api/accounts. The owner is always the verified AppAPI user; ids of other users' accounts
/// answer 404 exactly like ids that do not exist. No PATCH: the PHP proxy does not forward it.
/// </summary>
public static class AccountEndpoints
{
    public const string ConnectionTestRateLimit = "connection-test";

    public static void MapAccountEndpoints(this RouteGroupBuilder api)
    {
        var accounts = api.MapGroup("/accounts");

        accounts.MapGet("", async (ClaimsPrincipal user, AccountService service, CancellationToken ct) =>
            (await service.ListAsync(user.GetNextcloudUserId(), ct)).Select(AccountDto.From));

        accounts.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, AccountService service, CancellationToken ct) =>
            await service.GetAsync(user.GetNextcloudUserId(), id, ct) is { } account
                ? Results.Ok(AccountDto.From(account))
                : NotFound());

        accounts.MapPost("", async (AccountInput input, ClaimsPrincipal user, AccountService service, CancellationToken ct) =>
        {
            if (AccountValidation.Validate(input, passwordRequired: true) is { Count: > 0 } errors)
            {
                return Results.ValidationProblem(errors);
            }

            try
            {
                var account = await service.CreateAsync(user.GetNextcloudUserId(), input, ct);
                return Results.Created($"accounts/{account.Id}", AccountDto.From(account));
            }
            catch (AccountLimitException e)
            {
                return Results.Json(new { error = "account_limit", message = e.Message }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        accounts.MapPut("/{id:guid}", async (Guid id, AccountInput input, ClaimsPrincipal user, AccountService service, CancellationToken ct) =>
        {
            if (AccountValidation.Validate(input, passwordRequired: false) is { Count: > 0 } errors)
            {
                return Results.ValidationProblem(errors);
            }

            return await service.UpdateAsync(user.GetNextcloudUserId(), id, input, ct) is { } account
                ? Results.Ok(AccountDto.From(account))
                : NotFound();
        });

        accounts.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, AccountService service, CancellationToken ct) =>
            await service.DeleteAsync(user.GetNextcloudUserId(), id, ct) ? Results.NoContent() : NotFound());

        accounts.MapPut("/order", async (ReorderInput input, ClaimsPrincipal user, AccountService service, CancellationToken ct) =>
            await service.ReorderAsync(user.GetNextcloudUserId(), input.AccountIds ?? [], ct)
                ? Results.NoContent()
                : Results.Json(new { error = "invalid_order", message = "The list must contain each of your accounts exactly once." },
                    statusCode: StatusCodes.Status400BadRequest));

        accounts.MapPost("/test", async (ConnectionTestInput input, ClaimsPrincipal user, AccountService service, CancellationToken ct) =>
            {
                var accountInput = input.ToAccountInput();
                var passwordRequired = input.AccountId is null;
                if (AccountValidation.Validate(accountInput, passwordRequired) is { Count: > 0 } errors)
                {
                    return Results.ValidationProblem(errors);
                }

                return await service.TestAsync(user.GetNextcloudUserId(), accountInput, input.AccountId, ct) is { } result
                    ? Results.Ok(result)
                    : NotFound();
            })
            .RequireRateLimiting(ConnectionTestRateLimit);

        accounts.MapPost("/{id:guid}/test", async (Guid id, ClaimsPrincipal user, AccountService service, CancellationToken ct) =>
                await service.GetAsync(user.GetNextcloudUserId(), id, ct) is { } account
                    ? Results.Ok(await service.TestStoredAsync(account, ct))
                    : NotFound())
            .RequireRateLimiting(ConnectionTestRateLimit);
    }

    private static IResult NotFound() =>
        Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound);
}
