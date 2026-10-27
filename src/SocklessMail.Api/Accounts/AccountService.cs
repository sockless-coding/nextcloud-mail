using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mail;

namespace SocklessMail.Api.Accounts;

public sealed class AccountLimitException() : Exception($"A user can have at most {AccountService.MaxAccountsPerUser} accounts.");

/// <summary>
/// Account CRUD. Every method takes the owner explicitly and filters on it, in addition to the
/// DbContext's query filter. Another user's account is indistinguishable from a missing one.
/// </summary>
public sealed class AccountService(
    MailDbContext db, CredentialProtector credentials, ConnectionTester tester, ImapConnections imap, TimeProvider clock,
    ILogger<AccountService> logger)
{
    public const int MaxAccountsPerUser = 50;

    private IQueryable<MailAccount> Owned(string userId) => db.Accounts.Where(a => a.OwnerUserId == userId);

    public Task<List<MailAccount>> ListAsync(string userId, CancellationToken ct) =>
        Owned(userId).OrderBy(a => a.SortOrder).ThenBy(a => a.CreatedAt).ToListAsync(ct);

    public Task<MailAccount?> GetAsync(string userId, Guid id, CancellationToken ct) =>
        Owned(userId).SingleOrDefaultAsync(a => a.Id == id, ct);

    public async Task<MailAccount> CreateAsync(string userId, AccountInput input, CancellationToken ct)
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
            AuthMethod = MailAuthMethod.Password,
            ProtectedCredential = credentials.Protect(id, new PasswordCredential(input.Password!)),
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
        // Pooled connections were opened with the old settings or password.
        await imap.InvalidateAsync(account.Id);
        logger.LogInformation("Updated account {AccountId}", account.Id);
        return account;
    }

    public async Task<bool> DeleteAsync(string userId, Guid id, CancellationToken ct)
    {
        var deleted = await Owned(userId).Where(a => a.Id == id).ExecuteDeleteAsync(ct);
        if (deleted > 0)
        {
            await imap.InvalidateAsync(id);
            logger.LogInformation("Deleted account {AccountId}", id);
        }

        return deleted > 0;
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

            if (!TryGetCredential(account, out credential, out var unavailable))
            {
                return unavailable;
            }
        }

        return await tester.TestAsync(input.Imap.ToEndpoint(), input.Smtp.ToEndpoint(), input.Username.Trim(), credential, ct);
    }

    public async Task<ConnectionTestResult> TestStoredAsync(MailAccount account, CancellationToken ct)
    {
        if (!TryGetCredential(account, out var credential, out var unavailable))
        {
            return unavailable;
        }

        return await tester.TestAsync(
            new MailEndpoint(account.ImapHost, account.ImapPort, account.ImapSecurity),
            new MailEndpoint(account.SmtpHost, account.SmtpPort, account.SmtpSecurity),
            account.Username, credential, ct);
    }

    private bool TryGetCredential(MailAccount account, out MailCredential credential, out ConnectionTestResult unavailable)
    {
        try
        {
            credential = credentials.Unprotect(account.Id, account.ProtectedCredential);
            unavailable = null!;
            return true;
        }
        catch (CredentialUnavailableException e)
        {
            logger.LogWarning(e, "Stored credential of account {AccountId} cannot be decrypted", account.Id);
            var result = new ProtocolTestResult(false, "credential_unavailable", e.Message);
            credential = null!;
            unavailable = new ConnectionTestResult(result, result);
            return false;
        }
    }

    private static void Apply(MailAccount account, AccountInput input)
    {
        account.DisplayName = input.DisplayName.Trim();
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
