using Microsoft.EntityFrameworkCore;
using SocklessMail.Api.Sync;
using static SocklessMail.Tests.AccountTestData;

namespace SocklessMail.Tests;

/// <summary>How messages are grouped into conversations by their Message-ID, In-Reply-To and References.</summary>
public sealed class ConversationIndexTests : IDisposable
{
    private readonly TestApp _app = new();
    private Guid _accountId;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _app.Dispose();

    private static ConversationHeaders Message(string id, params string[] related) =>
        new(id, related.LastOrDefault(), related);

    /// <summary>Assigns in one sync of its own, as the cache does, and saves.</summary>
    private async Task<IReadOnlyList<Guid>> AssignAsync(params ConversationHeaders[] messages)
    {
        if (_accountId == Guid.Empty)
        {
            _accountId = await CreateAsync(_app.ClientAs("alice"));
        }

        await using var db = _app.CreateDbContext("alice");
        var ids = (await new ConversationIndex(db).AssignAsync(_accountId, "alice", messages, Guid.Empty, 0, Ct)).Ids;
        await db.SaveChangesAsync(Ct);
        return ids;
    }

    [Fact]
    public async Task Replies_join_the_message_they_reference()
    {
        var ids = await AssignAsync(Message("a"), Message("b", "a"), Message("c"));

        Assert.Equal(ids[0], ids[1]);
        Assert.NotEqual(ids[0], ids[2]);
    }

    [Fact]
    public async Task A_reply_seen_before_its_parent_still_shares_its_conversation()
    {
        var reply = await AssignAsync(Message("b", "a"));
        var parent = await AssignAsync(Message("a"));

        Assert.Equal(reply[0], parent[0]);
    }

    [Fact]
    public async Task A_message_that_ties_two_conversations_together_merges_them()
    {
        // Two branches seen without their common start: two conversations at first.
        var first = await AssignAsync(Message("b", "x"));
        var second = await AssignAsync(Message("c", "y"));
        Assert.NotEqual(first[0], second[0]);

        var joined = await AssignAsync(Message("d", "x", "y"));

        await using var db = _app.CreateDbContext("alice");
        var links = await db.ConversationLinks.Select(l => l.ConversationId).Distinct().ToListAsync(Ct);
        Assert.Equal(joined[0], Assert.Single(links));
    }

    [Fact]
    public async Task Merging_also_moves_the_cached_messages()
    {
        var first = await AssignAsync(Message("b", "x"));
        var folderId = Guid.NewGuid();
        await using (var db = _app.CreateDbContext("alice"))
        {
            db.Folders.Add(new CachedFolder { Id = folderId, AccountId = _accountId, OwnerUserId = "alice", FullName = "INBOX" });
            db.Messages.Add(new CachedMessage
            {
                FolderId = folderId, Uid = 1, OwnerUserId = "alice", Subject = "", From = [], To = [], SearchText = "",
                MessageIdHeader = "b", ConversationId = first[0],
            });
            await db.SaveChangesAsync(Ct);
        }

        await AssignAsync(Message("c", "y"));
        var joined = await AssignAsync(Message("d", "y", "x"));

        await using var check = _app.CreateDbContext("alice");
        Assert.Equal(joined[0], (await check.Messages.SingleAsync(Ct)).ConversationId);
    }

    [Fact]
    public async Task Cached_messages_moved_by_a_merge_are_changes_of_their_folder()
    {
        await AssignAsync(Message("b", "x"));
        // Newer, so the merge keeps the first conversation and moves this one's messages.
        var second = await AssignAsync(Message("c", "y"));
        var folderId = Guid.NewGuid();
        await using (var db = _app.CreateDbContext("alice"))
        {
            db.Folders.Add(new CachedFolder { Id = folderId, AccountId = _accountId, OwnerUserId = "alice", FullName = "Archive", ChangeSeq = 7 });
            db.Messages.Add(new CachedMessage
            {
                FolderId = folderId, Uid = 1, OwnerUserId = "alice", Subject = "", From = [], To = [], SearchText = "",
                MessageIdHeader = "c", ConversationId = second[0], ChangeSeq = 3,
            });
            await db.SaveChangesAsync(Ct);
        }

        ConversationAssignment assignment;
        await using (var db = _app.CreateDbContext("alice"))
        {
            assignment = await new ConversationIndex(db).AssignAsync(_accountId, "alice", [Message("d", "x", "y")], Guid.NewGuid(), 1, Ct);
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal("Archive", Assert.Single(assignment.ChangedFolders).FullName);
        Assert.False(assignment.ChangedHere);
        await using var check = _app.CreateDbContext("alice");
        var row = await check.Messages.SingleAsync(Ct);
        Assert.Equal(assignment.Ids[0], row.ConversationId);
        Assert.Equal(8, row.ChangeSeq);
        Assert.Equal(8, (await check.Folders.SingleAsync(Ct)).ChangeSeq);
    }

    [Fact]
    public async Task Merges_within_one_batch_reach_every_message_of_it()
    {
        var ids = await AssignAsync(Message("b", "x"), Message("c", "y"), Message("d", "x", "y"));

        Assert.Single(ids.Distinct());
    }

    [Fact]
    public async Task A_message_without_any_id_stands_alone()
    {
        var ids = await AssignAsync(new ConversationHeaders("", null, []), new ConversationHeaders("", null, []));

        Assert.NotEqual(ids[0], ids[1]);
    }

    [Fact]
    public void Ids_lose_their_angle_brackets_and_overlong_ones_are_dropped()
    {
        Assert.Equal("a", ConversationHeaders.Clean(" <a> "));
        Assert.Null(ConversationHeaders.Clean("<>"));
        Assert.Null(ConversationHeaders.Clean(new string('x', ConversationIndex.MaxIdLength + 1)));
    }
}
