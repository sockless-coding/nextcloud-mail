using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using SocklessMail.Api.Sync;

namespace SocklessMail.Tests;

/// <summary>The push channel (/api/hub): who may connect, and who gets which nudge.</summary>
public sealed class PushTests : IDisposable
{
    private readonly TestApp _app = new();

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Connecting_requires_a_nextcloud_user()
    {
        await using var anonymous = _app.HubAs(null);
        await Assert.ThrowsAsync<HttpRequestException>(() => anonymous.StartAsync(Ct));
    }

    [Fact]
    public async Task Folder_changes_reach_only_the_owners_connections()
    {
        await using var alice = _app.HubAs("alice");
        await using var bob = _app.HubAs("bob");
        var aliceGot = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bobGot = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        alice.On<JsonElement>(MailHub.FolderChanged, nudge => aliceGot.TrySetResult(nudge));
        bob.On<JsonElement>(MailHub.FolderChanged, nudge => bobGot.TrySetResult(nudge));
        await alice.StartAsync(Ct);
        await bob.StartAsync(Ct);

        var account = Guid.NewGuid();
        await _app.Services.GetRequiredService<MailPush>().FolderChangedAsync("alice", account, "INBOX");

        var nudge = await aliceGot.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal(account, nudge.GetProperty("accountId").GetGuid());
        Assert.Equal("SU5CT1g", nudge.GetProperty("folderId").GetString());
        await Task.Delay(300, Ct);
        Assert.False(bobGot.Task.IsCompleted);
    }

    [Fact]
    public void An_open_connection_keeps_the_user_active()
    {
        var clock = new FakeTimeProvider();
        var activity = new UserActivity(new WatcherOptions(), clock);

        activity.Connected("alice");
        activity.Connected("alice");
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(["alice"], activity.ActiveUsers());

        // Active until the last tab closes, then for the usual window.
        activity.Disconnected("alice");
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(["alice"], activity.ActiveUsers());
        activity.Disconnected("alice");
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(["alice"], activity.ActiveUsers());
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Empty(activity.ActiveUsers());
    }
}
