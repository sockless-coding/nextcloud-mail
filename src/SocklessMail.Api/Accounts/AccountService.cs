using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mail;
using SocklessMail.Api.OAuth;
using SocklessMail.Api.Sync;

namespace SocklessMail.Api.Accounts;

public sealed class AccountLimitException() : Exception($"A user can have at most {AccountService.MaxAccountsPerUser} accounts.");

/// <summary>
/// Account CRUD. Every method takes the owner explicitly and filters on it, in addition to the
/// DbContext's query filter. Another user's account is indistinguishable from a missing one.
/// </summary>
public sealed class AccountService(
    MailDbContext db, CredentialProtector credentials, MailLogins logins, ConnectionTester tester, ImapConnections imap,
    MailboxWatchers watchers, TimeProvider clock, ILogger<AccountService> logger)
{
    public const int MaxAccountsPerUser = 50;

    private IQueryable<MailAccount> Owned(string userId) => db.Accounts.Where(a => a.OwnerUserId == userId);

    public Task<List<MailAccount>> ListAsync(string userId, CancellationToken ct) =>
        Owned(userId).OrderBy(a => a.SortOrder).ThenBy(a => a.CreatedAt).ToListAsync(ct);

    public Task<MailAccount?> GetAsync(string userId, Guid id, CancellationToken ct) =>
        Owned(userId).SingleOrDefaultAsync(a => a.Id == id, ct);

    public Task<MailAccount> CreateAsync(string userId, AccountInput input, CancellationToken ct) =>
        CreateAsync(userId, input, MailAuthMethod.Password, new PasswordCredential(input.Password!), ct);

    /// <summary>A new account signed in with OAuth2: the provider's servers, the address that signed in.</summary>
    public Task<MailAccount> CreateOAuthAsync(
        string userId, OAuthProvider provider, OAuthIdentity identity, string refreshToken, string? displayName, string? senderName, CancellationToken ct)
    {
        var input = new AccountInput(
            string.IsNullOrWhiteSpace(displayName) ? identity.Email : displayName.Trim(),
            identity.Email,
            identity.Email,
            null,
            provider.Imap,
            provider.Smtp,
            string.IsNullOrWhiteSpace(senderName) ? identity.Name : senderName.Trim());
        return CreateAsync(userId, input, MailAuthMethod.OAuth2, new OAuthCredential(provider.Id, refreshToken), ct);
    }

    private async Task<MailAccount> CreateAsync(string userId, AccountInput input, MailAuthMethod method, MailCredential credential, CancellationToken ct)
    {
        var existing = await Owned(userId).Select(a => (int?)a.SortOrder).ToListAsync(ct);
        if (existing.Count >= MaxAccountsPerUser)
        {
            throw new AccountLimitException();
        }

        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7();
        var account = new MailAccount
        {
            Id = id,
            OwnerUserId = userId,
            SortOrder = (existing.Max() ?? -1) + 1,
            DisplayName = "",
            EmailAddress = "",
            ImapHost = "",
            SmtpHost = "",
            Username = "",
            AuthMethod = method,
            ProtectedCredential = credentials.Protect(id, credential),
            CreatedAt = now,
            UpdatedAt = now,
        };
        Apply(account, input);
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Created account {AccountId}", account.Id);
        return account;
    }

    public async Task<MailAccount?> UpdateAsync(string userId, Guid id, AccountInput input, CancellationToken ct)
    {
        var account = await GetAsync(userId, id, ct);
        if (account is null)
        {
            return null;
        }

        Apply(account, input);
        if (!string.IsNullOrEmpty(input.Password))
        {
            account.AuthMethod = MailAuthMethod.Password;
            account.ProtectedCredential = credentials.Protect(account.Id, new PasswordCredential(input.Password));
        }

        account.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await ForgetConnectionsAsync(account.Id);
        logger.LogInformation("Updated account {AccountId}", account.Id);
        return account;
    }

    /// <summary>Signing in again: replaces the account's OAuth grant.</summary>
    public async Task<MailAccount?> ReplaceGrantAsync(string userId, Guid id, OAuthCredential grant, CancellationToken ct)
    {
        var account = await GetAsync(userId, id, ct);
        if (account is null)
        {
            return null;
        }

        account.AuthMethod = MailAuthMethod.OAuth2;
        account.ProtectedCredential = credentials.Protect(account.Id, grant);
        account.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await ForgetConnectionsAsync(account.Id);
        logger.LogInformation("Signed in again to account {AccountId}", account.Id);
        return account;
    }

    /// <summary>Pooled and watching connections, and cached tokens, belong to the old settings or credential.</summary>
    private async Task ForgetConnectionsAsync(Guid accountId)
    {
        await imap.InvalidateAsync(accountId);
        watchers.Invalidate(accountId);
        logins.Forget(accountId);
    }

    public async Task<bool> DeleteAsync(string userId, Guid id, CancellationToken ct)
    {
        var deleted = await Owned(userId).Where(a => a.Id == id).ExecuteDeleteAsync(ct);
        if (deleted > 0)
        {
            await ForgetConnectionsAsync(id);
            logger.LogInformation("Deleted account {AccountId}", id);
        }

        return deleted > 0;
    }

    /// <summary>
    /// How the account is shown (colour, badge, part of "All accounts"). Not a change of its
    /// settings: connections stay and UpdatedAt is left alone.
    /// </summary>
    public async Task<MailAccount?> SetAppearanceAsync(string userId, Guid id, AppearanceInput input, CancellationToken ct)
    {
        var account = await GetAsync(userId, id, ct);
        if (account is null)
        {
            return null;
        }

        static string? OrNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        account.Color = OrNull(input.Color)?.ToLowerInvariant();
        account.BadgeText = OrNull(input.BadgeText);
        account.BadgeIcon = OrNull(input.BadgeIcon);
        account.IncludeInUnified = input.IncludeInUnified;
        await db.SaveChangesAsync(ct);
        return account;
    }

    /// <summary>Turns conversations on or off; like the appearance, not a change of the account's settings.</summary>
    public async Task<MailAccount?> SetConversationsAsync(string userId, Guid id, bool enabled, CancellationToken ct)
    {
        var account = await GetAsync(userId, id, ct);
        if (account is null)
        {
            return null;
        }

        account.Conversations = enabled;
        await db.SaveChangesAsync(ct);
        return account;
    }

    /// <summary>Sets the order; <paramref name="ids"/> must be exactly the user's accounts.</summary>
    public async Task<bool> ReorderAsync(string userId, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var accounts = await Owned(userId).ToListAsync(ct);
        if (ids.Count != accounts.Count || ids.Distinct().Count() != ids.Count
            || !accounts.All(a => ids.Contains(a.Id)))
        {
            return false;
        }

        var order = ids.ToList();
        foreach (var account in accounts)
        {
            account.SortOrder = order.IndexOf(account.Id);
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Tests settings that may not be saved yet. Without a password, the stored credential of
    /// <paramref name="accountId"/> is used (editing an account without retyping the password).
    /// </summary>
    /// <returns>null when <paramref name="accountId"/> is not one of the user's accounts.</returns>
    public async Task<ConnectionTestResult?> TestAsync(string userId, AccountInput input, Guid? accountId, CancellationToken ct)
    {
        MailCredential credential;
        if (!string.IsNullOrEmpty(input.Password))
        {
            credential = new PasswordCredential(input.Password);
        }
        else
        {
            var account = accountId is { } id ? await GetAsync(userId, id, ct) : null;
            if (account is null)
            {
                return null;
            }

            var (stored, failure) = await StoredCredentialAsync(account, ct);
            if (stored is null)
            {
                return failure;
            }

            credential = stored;
        }

        return await tester.TestAsync(input.Imap.ToEndpoint(), input.Smtp.ToEndpoint(), input.Username.Trim(), credential, ct);
    }

    public async Task<ConnectionTestResult> TestStoredAsync(MailAccount account, CancellationToken ct)
    {
        var (credential, failure) = await StoredCredentialAsync(account, ct);
        if (credential is null)
        {
            return failure!;
        }

        return await tester.TestAsync(
            new MailEndpoint(account.ImapHost, account.ImapPort, account.ImapSecurity),
            new MailEndpoint(account.SmtpHost, account.SmtpPort, account.SmtpSecurity),
            account.Username, credential, ct);
    }

    /// <summary>What the stored credential logs in with, or why there is nothing to log in with (no server was asked).</summary>
    private async Task<(MailCredential? Credential, ConnectionTestResult? Failure)> StoredCredentialAsync(MailAccount account, CancellationToken ct)
    {
        try
        {
            return (await logins.ForConnectionAsync(account, ct), null);
        }
        catch (CredentialUnavailableException e)
        {
            logger.LogWarning(e, "Stored credential of account {AccountId} cannot be decrypted", account.Id);
            return (null, Failed("credential_unavailable", e.Message));
        }
        catch (MailConnectionException e)
        {
            // An OAuth grant that did not yield an access token.
            return (null, Failed(e.Code, e.Message));
        }
    }

    private static ConnectionTestResult Failed(string code, string message)
    {
        var result = new ProtocolTestResult(false, code, message);
        return new ConnectionTestResult(result, result);
    }

    private static void Apply(MailAccount account, AccountInput input)
    {
        account.DisplayName = input.DisplayName.Trim();
        account.SenderName = string.IsNullOrWhiteSpace(input.SenderName) ? null : input.SenderName.Trim();
        account.EmailAddress = input.EmailAddress.Trim();
        account.Username = input.Username.Trim();
        account.ImapHost = input.Imap.Host.Trim();
        account.ImapPort = input.Imap.Port;
        account.ImapSecurity = input.Imap.Security;
        account.SmtpHost = input.Smtp.Host.Trim();
        account.SmtpPort = input.Smtp.Port;
        account.SmtpSecurity = input.Smtp.Security;
    }
}
