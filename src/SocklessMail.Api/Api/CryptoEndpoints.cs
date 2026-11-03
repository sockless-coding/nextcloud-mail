using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.AppApi;
using SocklessMail.Api.Contacts;
using SocklessMail.Api.Crypto;
using SocklessMail.Api.Mailbox;

namespace SocklessMail.Api.Api;

/// <summary>The user's OpenPGP keys and S/MIME certificates: list, import, make, export, unlock, delete.</summary>
public static class CryptoEndpoints
{
    public static void MapCryptoEndpoints(this RouteGroupBuilder api)
    {
        var keys = api.MapGroup("/crypto/keys");

        keys.MapGet("", (ClaimsPrincipal user, CryptoKeyStore store, CancellationToken ct) => store.ListAsync(user.GetNextcloudUserId(), ct));

        // Which keys the recipients of a message can be encrypted to; with search, also looked for
        // in Contacts and on the internet (their domain's WKD, keys.openpgp.org).
        api.MapPost("/crypto/recipients", async (RecipientLookupInput input, ClaimsPrincipal user, KeyDirectory directory, CancellationToken ct) =>
        {
            var addresses = input.Addresses ?? [];
            return addresses.Count > KeyDirectory.MaxAddresses
                ? Invalid(new MailboxValidationException("addresses", L.T("Ask for at most {max} addresses.", ("max", KeyDirectory.MaxAddresses))))
                : Results.Ok(await directory.LookupAsync(user.GetNextcloudUserId(), addresses, input.Search, ct));
        });

        keys.MapPost("/import", async (ImportKeyInput input, ClaimsPrincipal user, CryptoKeyStore store, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await store.ImportAsync(user.GetNextcloudUserId(), input, ct));
            }
            catch (MailboxValidationException e)
            {
                return Invalid(e);
            }
        });

        keys.MapPost("/generate", async (
            GenerateKeyInput input, ClaimsPrincipal user, CryptoKeyStore store, AccountService accounts, CancellationToken ct) =>
        {
            var userId = user.GetNextcloudUserId();
            var addresses = (await accounts.ListAsync(userId, ct)).Select(a => a.EmailAddress).ToList();
            try
            {
                return Results.Ok(await store.GenerateAsync(userId, input, addresses, ct));
            }
            catch (MailboxValidationException e)
            {
                return Invalid(e);
            }
        });

        keys.MapGet("/{id:guid}/public", async (Guid id, ClaimsPrincipal user, CryptoKeyStore store, HttpContext http, CancellationToken ct) =>
        {
            if (await store.FindAsync(user.GetNextcloudUserId(), id, ct) is not { } key)
            {
                return Results.NotFound();
            }

            var (fileName, contentType, text) = CryptoKeyStore.PublicExport(key);
            http.Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = fileName }.ToString();
            http.Response.Headers.ContentSecurityPolicy = MailboxEndpoints.ContentSandbox;
            return Results.Text(text, contentType, Encoding.ASCII);
        });

        keys.MapPost("/{id:guid}/unlock", async (Guid id, UnlockKeyInput input, ClaimsPrincipal user, CryptoKeyStore store, CancellationToken ct) =>
        {
            var userId = user.GetNextcloudUserId();
            if (await store.FindAsync(userId, id, ct) is not { } key)
            {
                return Results.NotFound();
            }

            try
            {
                return store.Unlock(userId, key, input.Passphrase ?? "")
                    ? Results.Ok((await store.ListAsync(userId, ct)).First(k => k.Id == id))
                    : Invalid(new MailboxValidationException("passphrase", L.T("The passphrase is wrong.")));
            }
            catch (MailboxValidationException e)
            {
                return Invalid(e);
            }
            catch (KeyUnavailableException e)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict, extensions: new Dictionary<string, object?> { ["error"] = "key_unavailable" });
            }
        });

        // Locks every key of the user and forgets what was decrypted with them.
        keys.MapPost("/lock", (ClaimsPrincipal user, CryptoKeyStore store) =>
        {
            store.LockAll(user.GetNextcloudUserId());
            return Results.NoContent();
        });

        // Into the contact with the key's address (a new contact when there is none), as a vCard KEY.
        keys.MapPost("/{id:guid}/contact", async (Guid id, ClaimsPrincipal user, CryptoKeyStore store, KeySharing sharing, CancellationToken ct) =>
        {
            var userId = user.GetNextcloudUserId();
            if (await store.FindAsync(userId, id, ct) is not { } key)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await sharing.SaveToContactAsync(userId, key, ct));
            }
            catch (MailboxValidationException e)
            {
                return Invalid(e);
            }
            catch (NextcloudContactsException e)
            {
                return Results.Json(new { error = e.Code, message = e.Message }, statusCode: e.StatusCode);
            }
        });

        // Only on the user's request: keys.openpgp.org then mails each address a link to confirm it.
        keys.MapPost("/{id:guid}/publish", async (Guid id, ClaimsPrincipal user, CryptoKeyStore store, KeySharing sharing, CancellationToken ct) =>
        {
            if (await store.FindAsync(user.GetNextcloudUserId(), id, ct) is not { } key)
            {
                return Results.NotFound();
            }

            try
            {
                return Results.Ok(await sharing.PublishAsync(key, ct));
            }
            catch (MailboxValidationException e)
            {
                return Invalid(e);
            }
            catch (KeyServerException e)
            {
                return Results.Json(new { error = "key_server", message = e.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // A key attached to a message (.asc, application/pgp-keys, a certificate): imported like a file.
        api.MapPost("/accounts/{accountId:guid}/folders/{folderId}/messages/{messageId}/parts/{partId}/import-key", async (
                Guid accountId, string folderId, string messageId, string partId,
                ClaimsPrincipal user, AccountService accounts, MailboxService mailbox, CryptoKeyStore store, CancellationToken ct) =>
            {
                var userId = user.GetNextcloudUserId();
                if (await accounts.GetAsync(userId, accountId, ct) is not { } account)
                {
                    return MailboxEndpoints.NotFound();
                }

                var part = await mailbox.GetPartAsync(account, folderId, messageId, partId, ct);
                using var buffer = new MemoryStream();
                await part.WriteToAsync(buffer, ct);
                if (buffer.Length > CryptoKeyStore.MaxImportBytes)
                {
                    return Invalid(new MailboxValidationException("data", L.T("The attachment is too large for a key.")));
                }

                try
                {
                    return Results.Ok(await store.ImportAsync(userId, new ImportKeyInput(null, Convert.ToBase64String(buffer.ToArray()), null), ct));
                }
                catch (MailboxValidationException e) when (e.Field == "passphrase")
                {
                    return Invalid(new MailboxValidationException("data", L.T("This is a private key. Import it under Settings → Encryption, with its passphrase.")));
                }
            })
            .AddEndpointFilter<MailboxEndpoints.MailErrorFilter>();

        keys.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, CryptoKeyStore store, CancellationToken ct) =>
            await store.DeleteAsync(user.GetNextcloudUserId(), id, ct) ? Results.NoContent() : Results.NotFound());
    }

    private static IResult Invalid(MailboxValidationException e) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [e.Field] = [e.Message] });
}
