using System.Net.Http.Json;
using System.Text.Json;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using SocklessMail.Api.Data;
using SocklessMail.Api.Sync;
using static SocklessMail.IntegrationTests.MailApp;

namespace SocklessMail.IntegrationTests;

/// <summary>IDLE watchers: new mail lands in the cache without any request asking the server.</summary>
public sealed class WatcherTests(GreenMailFixture greenMail, DovecotFixture dovecot) : IDisposable
{
    private const string UserId = "watched-user";
    private const string GreenMailUser = "watcher@example.test";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly MailApp _app = new(services => services.AddSingleton(new WatcherOptions
    {
        MaxWatchers = 5,
        ReconcileInterval = TimeSpan.FromMilliseconds(500),
        FirstRetryDelay = TimeSpan.FromSeconds(1),
    }));

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _app.Dispose();

    public static TheoryData<string> Servers() => ["dovecot", "greenmail"];

    private async Task<(Guid Account, ImapClient Imap)> SetUpAsync(HttpClient client, string server)
    {
        if (server == "dovecot")
        {
            var user = DovecotFixture.NewUser();
            return (await AddAccountAsync(client, dovecot, user), await dovecot.ConnectAsync(user));
        }

        var imap = new ImapClient();
        await imap.ConnectAsync(greenMail.Host, greenMail.Imap, SecureSocketOptions.None, Ct);
        await imap.AuthenticateAsync(GreenMailUser, GreenMailFixture.ActionsPassword, Ct);
        return (await AddAccountAsync(client, greenMail, GreenMailUser, GreenMailFixture.ActionsPassword), imap);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting until {what}");
            await Task.Delay(100);
        }
    }

    private async Task<bool> CachedAsync(string subject)
    {
        await using var scope = _app.Services.GetRequiredService<IServiceScopeFactory>().CreateUserScope(UserId);
        return await scope.ServiceProvider.GetRequiredService<MailDbContext>().Messages.AnyAsync(m => m.Subject == subject);
    }

    private async Task<DateTimeOffset?> SyncedAtAsync(Guid account)
    {
        await using var scope = _app.Services.GetRequiredService<IServiceScopeFactory>().CreateUserScope(UserId);
        return await scope.ServiceProvider.GetRequiredService<MailDbContext>().Folders
            .Where(f => f.AccountId == account && f.FullName == "INBOX").Select(f => f.SyncedAt).SingleAsync();
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task New_mail_reaches_the_cache_while_idling(string server)
    {
        var client = _app.ClientAs(UserId);
        var (account, imap) = await SetUpAsync(client, server);
        using var _ = imap;
        var watchers = _app.Services.GetRequiredService<MailboxWatchers>();

        // Any request marks the user active; the watcher starts, syncs the inbox and idles.
        var page = await Json(await client.GetAsync($"/api/accounts/{account}/folders/SU5CT1g/messages", Ct));
        await WaitUntilAsync(() => Task.FromResult(watchers.IsCurrent(account, "INBOX")), "the inbox is watched");

        // An open tab hears about the new mail without asking.
        await using var hub = _app.HubAs(UserId);
        var pushed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<JsonElement>(MailHub.FolderChanged, nudge => pushed.TrySetResult(nudge));
        await hub.StartAsync(Ct);

        var subject = $"Pushed {Guid.NewGuid():N}";
        var message = new MimeMessage { Subject = subject, Body = new TextPart("plain") { Text = "hi" } };
        message.From.Add(new MailboxAddress("", "sender@example.test"));
        await imap.Inbox.AppendAsync(message, MessageFlags.None, Ct);

        await WaitUntilAsync(() => CachedAsync(subject), "the watcher cached the new message");
        var nudge = await pushed.Task.WaitAsync(Patience, Ct);
        Assert.Equal(account, nudge.GetProperty("accountId").GetGuid());
        Assert.Equal("SU5CT1g", nudge.GetProperty("folderId").GetString());
        await WaitUntilAsync(() => Task.FromResult(watchers.IsCurrent(account, "INBOX")), "the watcher idles again");

        // Idling means idling: no syncs while nothing changes.
        var syncedAt = await SyncedAtAsync(account);
        await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        Assert.Equal(syncedAt, await SyncedAtAsync(account));
        Assert.True(watchers.IsCurrent(account, "INBOX"));

        var changes = await Json(await client.GetAsync(
            $"/api/accounts/{account}/folders/SU5CT1g/changes?since={page.GetProperty("token").GetInt64()}", Ct));
        Assert.Contains(subject, changes.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("subject").GetString()));

        // Deleting the account stops its watcher.
        (await client.DeleteAsync($"/api/accounts/{account}", Ct)).EnsureSuccessStatusCode();
        await WaitUntilAsync(() => Task.FromResult(watchers.Count == 0), "the watcher stopped");
    }
}
