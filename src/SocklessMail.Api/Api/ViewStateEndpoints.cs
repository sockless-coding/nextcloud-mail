using System.Security.Claims;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.AppApi;

namespace SocklessMail.Api.Api;

public sealed record ViewStateInput(string? Storage, ViewStateDto? State);

/// <summary>The folder pane's remembered state (expanded folders, last folder per account, last view).</summary>
public static class ViewStateEndpoints
{
    public static void MapViewStateEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/view-state", (ClaimsPrincipal user, ViewStates states, CancellationToken ct) =>
            states.GetAsync(user.GetNextcloudUserId(), ct));

        api.MapPut("/view-state", async (ViewStateInput input, ClaimsPrincipal user, ViewStates states, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await states.SaveAsync(user.GetNextcloudUserId(), input.Storage ?? ViewStates.Server, input.State, ct));
            }
            catch (ViewStateValidationException e)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [e.Field] = [e.Message] });
            }
        });
    }
}
