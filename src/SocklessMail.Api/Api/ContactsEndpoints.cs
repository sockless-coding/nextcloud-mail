using System.Security.Claims;
using MimeKit;
using SocklessMail.Api.AppApi;
using SocklessMail.Api.Contacts;

namespace SocklessMail.Api.Api;

public sealed record CreateContactInput(string? AddressBookId, string? Name, string? Address);

public sealed record AddAddressInput(string? ContactId, string? Address);

/// <summary>Nextcloud Contacts: recipient suggestions and adding senders to an address book.</summary>
public static class ContactsEndpoints
{
    public static void MapContactsEndpoints(this RouteGroupBuilder api)
    {
        var contacts = api.MapGroup("/contacts").AddEndpointFilter<ContactsErrorFilter>();

        contacts.MapGet("/search", async (string? q, ClaimsPrincipal user, ContactDirectory directory, NextcloudContacts dav, CancellationToken ct) =>
        {
            var query = (q ?? "").Trim();
            if (query.Length is 0 or > 100)
            {
                return Results.Ok(new ContactSearchResult([], []));
            }

            return Results.Ok(ContactDirectory.Search(await directory.GetAsync(user.GetNextcloudUserId(), dav, ct), query));
        });

        contacts.MapGet("/by-address", async (string? address, ClaimsPrincipal user, ContactDirectory directory, NextcloudContacts dav, CancellationToken ct) =>
            Results.Ok(ParseAddress(address) is { } a
                ? ContactDirectory.ByAddress(await directory.GetAsync(user.GetNextcloudUserId(), dav, ct), a)
                : []));

        contacts.MapGet("/address-books", async (ClaimsPrincipal user, ContactDirectory directory, NextcloudContacts dav, CancellationToken ct) =>
            Results.Ok((await directory.GetAsync(user.GetNextcloudUserId(), dav, ct))
                .Select(b => new { id = b.Book.Id, name = b.Book.Name, writable = b.Book.Writable })));

        contacts.MapPost("", async (CreateContactInput input, ClaimsPrincipal user, ContactDirectory directory, NextcloudContacts dav, CancellationToken ct) =>
        {
            if (ParseAddress(input.Address) is not { } address)
            {
                return Invalid("address", "Enter a valid email address.");
            }

            var name = (input.Name ?? "").Trim();
            if (name.Length > 200)
            {
                return Invalid("name", "Names may be at most 200 characters.");
            }

            var userId = user.GetNextcloudUserId();
            // The list as Nextcloud has it now, not as cached: the book must exist and be writable.
            directory.Invalidate(userId);
            var book = (await directory.GetAsync(userId, dav, ct)).Select(b => b.Book).FirstOrDefault(b => b.Id == input.AddressBookId);
            if (book is null || !book.Writable)
            {
                return Invalid("addressBookId", "Choose an address book you can add contacts to.");
            }

            var id = await dav.CreateAsync(userId, book.Id, name.Length > 0 ? name : address, address, ct);
            directory.Invalidate(userId);
            return Results.Ok(new { id });
        });

        contacts.MapPost("/addresses", async (AddAddressInput input, ClaimsPrincipal user, ContactDirectory directory, NextcloudContacts dav, CancellationToken ct) =>
        {
            if (ParseAddress(input.Address) is not { } address)
            {
                return Invalid("address", "Enter a valid email address.");
            }

            var userId = user.GetNextcloudUserId();
            await dav.AddEmailAsync(userId, input.ContactId ?? "", address, ct);
            directory.Invalidate(userId);
            return Results.Ok(new { id = input.ContactId });
        });
    }

    /// <summary>A bare address ("a@b.example"), or null.</summary>
    private static string? ParseAddress(string? text) =>
        MailboxAddress.TryParse(ParserOptions.Default, (text ?? "").Trim(), out var parsed)
        && parsed.Address.Contains('@') && parsed.Address.Length <= 254 && !parsed.Address.Any(char.IsWhiteSpace)
            ? parsed.Address
            : null;

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });

    private sealed class ContactsErrorFilter : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            try
            {
                return await next(context);
            }
            catch (NextcloudContactsException e)
            {
                return Results.Json(new { error = e.Code, message = e.Message }, statusCode: e.StatusCode);
            }
        }
    }
}
