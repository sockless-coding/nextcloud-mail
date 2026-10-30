using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using SocklessMail.Api.Accounts;
using SocklessMail.Api.Data;
using SocklessMail.Api.Mailbox;
using SocklessMail.Api.OAuth;
using SocklessMail.Api.Tags;
using static SocklessMail.IntegrationTests.MailApp;

namespace SocklessMail.IntegrationTests;

/// <summary>
/// Tags as Outlook categories: IMAP against Dovecot (standing in for Exchange), Microsoft Graph
/// faked in memory with the messages Dovecot holds (same Message-IDs), and Graph tokens faked.
/// </summary>
public sealed partial class OutlookCategoryTests(DovecotFixture dovecot) : IAsyncLifetime
{
    private const string UserId = "outlook-user";
    private static readonly string Inbox = MailboxIds.FolderId("INBOX");
    private static readonly string Archive = MailboxIds.FolderId("Archive");

    private readonly FakeGraph _graph = new();
    private readonly FakeGraphTokens _tokens = new();
    private MailApp _app = null!;
    private HttpClient _client = null!;
    private ImapClient _imap = null!;
    private Guid _account;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _app = new MailApp(services =>
        {
            services.AddSingleton<IGraphTokens>(_tokens);
            services.AddHttpClient(GraphClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _graph);
        });
        var user = DovecotFixture.NewUser();
        _imap = await dovecot.ConnectAsync(user);
        await _imap.GetFolder(_imap.PersonalNamespaces[0]).CreateAsync("Archive", true, Ct);
        _client = _app.ClientAs(UserId);
        _account = await AddAccountAsync(_client, dovecot, user);
    }

    public async ValueTask DisposeAsync()
    {
        _imap.Dispose();
        await _app.DisposeAsync();
    }

    /// <summary>A message in Dovecot and the same message in "Graph".</summary>
    private async Task<string> DeliverAsync(string subject, string folderName = "INBOX", params string[] categories)
    {
        var messageId = $"{Guid.NewGuid():N}@example.test";
        var message = new MimeMessage { Subject = subject, MessageId = messageId, Body = new TextPart("plain") { Text = subject } };
        message.From.Add(new MailboxAddress("Sender", "sender@example.test"));
        message.To.Add(new MailboxAddress("", "me@example.test"));
        var folder = folderName == "INBOX" ? _imap.Inbox : await _imap.GetFolderAsync(folderName, Ct);
        await folder.OpenAsync(FolderAccess.ReadWrite, Ct);
        var uid = await folder.AppendAsync(message, MessageFlags.None, Ct);
        _graph.Add(messageId, subject, categories);
        return MailboxIds.MessageId(folder.UidValidity, uid!.Value.Id);
    }

    private async Task<JsonElement> CreateTagAsync(string name, string color = "#0078d4") =>
        await Json(await _client.PostAsJsonAsync("/api/tags", new { name, color }, Ct));

    private async Task<Dictionary<string, string[]>> ListedAsync(string folderId) =>
        (await Json(await _client.GetAsync($"{Base}/folders/{folderId}/messages", Ct))).GetProperty("messages").EnumerateArray()
        .ToDictionary(m => m.GetProperty("subject").GetString()!, m => m.GetProperty("keywords").EnumerateArray().Select(k => k.GetString()!).ToArray());

    private async Task<List<(string Subject, string Folder)>> TagFolderAsync(Guid tag) =>
        (await Json(await _client.GetAsync($"/api/tags/{tag}/messages", Ct))).GetProperty("messages").EnumerateArray()
        .Select(h => (h.GetProperty("message").GetProperty("subject").GetString()!, h.GetProperty("folderFullName").GetString()!))
        .ToList();

    private Task<JsonElement> TagAsync(string id, Guid? add = null, Guid? remove = null) =>
        _client.PutAsJsonAsync($"{Base}/folders/{Inbox}/messages/tags",
            new { messageIds = new[] { id }, add = add is null ? null : new[] { add.Value }, remove = remove is null ? null : new[] { remove.Value } }, Ct)
            .ContinueWith(t => Json(t.Result), Ct).Unwrap();

    private string Base => $"/api/accounts/{_account}";

    private Task ScanAsync() => _app.Services.GetRequiredService<TagScanner>().ScanUserAsync(UserId, Ct);

    private async Task<TagStorage> StorageAsync()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MailDbContext>().Accounts.IgnoreQueryFilters()
            .Where(a => a.Id == _account).Select(a => a.TagStorage).SingleAsync(Ct);
    }

    private async Task<HashSet<string>> ImapKeywordsAsync(string subject)
    {
        await _imap.Inbox.OpenAsync(FolderAccess.ReadOnly, Ct);
        var uids = await _imap.Inbox.SearchAsync(SearchQuery.SubjectContains(subject), Ct);
        return (await _imap.Inbox.FetchAsync(uids, MessageSummaryItems.Flags, Ct)).Single().Keywords.ToHashSet();
    }

    [Fact]
    public async Task A_tag_is_an_outlook_category_with_the_nearest_color()
    {
        var tag = await CreateTagAsync("Invoices", "#0082c9");
        var tagId = tag.GetProperty("id").GetGuid();
        var id = await DeliverAsync("Bill");
        await ListedAsync(Inbox);

        var result = await TagAsync(id, add: tagId);

        Assert.Equal(["$invoices"], result.GetProperty("keywords").GetProperty(id).EnumerateArray().Select(k => k.GetString()));
        Assert.Equal(TagStorage.Graph, await StorageAsync());
        Assert.Equal(["Invoices"], _graph.CategoriesOf("Bill"));
        Assert.Equal("preset7", _graph.Master["Invoices"]);
        Assert.DoesNotContain("$invoices", await ImapKeywordsAsync("Bill"));
        Assert.Equal(["$invoices"], (await ListedAsync(Inbox))["Bill"]);
        Assert.Equal([("Bill", "INBOX")], await TagFolderAsync(tagId));

        await TagAsync(id, remove: tagId);
        Assert.Empty(_graph.CategoriesOf("Bill"));
        Assert.Empty((await ListedAsync(Inbox))["Bill"]);
    }

    [Fact]
    public async Task Categories_set_in_outlook_show_up_after_a_scan_and_go_when_removed()
    {
        var tagId = (await CreateTagAsync("Travel")).GetProperty("id").GetGuid();
        await DeliverAsync("Plain");
        await TagAsync(await DeliverAsync("Starter"), add: tagId);
        await DeliverAsync("Flight", "Archive", "Travel", "Personal");

        await ScanAsync();

        Assert.Contains(("Flight", "Archive"), await TagFolderAsync(tagId));
        Assert.Equal(["$travel"], (await ListedAsync(Archive))["Flight"]);

        _graph.SetCategories("Flight", "Personal");
        await ScanAsync();
        Assert.DoesNotContain(("Flight", "Archive"), await TagFolderAsync(tagId));
        Assert.Empty((await ListedAsync(Archive))["Flight"]);
    }

    [Fact]
    public async Task Renaming_a_tag_renames_the_category_and_a_failure_keeps_the_old_name()
    {
        var tagId = (await CreateTagAsync("Work")).GetProperty("id").GetGuid();
        await TagAsync(await DeliverAsync("Report"), add: tagId);

        await Json(await _client.PutAsJsonAsync($"/api/tags/{tagId}", new { name = "Office" }, Ct));
        Assert.Equal(["Office"], _graph.CategoriesOf("Report"));
        Assert.True(_graph.Master.ContainsKey("Office"));
        Assert.False(_graph.Master.ContainsKey("Work"));

        _graph.Down = true;
        var failed = await _client.PutAsJsonAsync($"/api/tags/{tagId}", new { name = "Job" }, Ct);
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        _graph.Down = false;
        var tags = await Json(await _client.GetAsync("/api/tags", Ct));
        Assert.Equal("Office", tags[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task App_kept_tags_move_to_outlook_once_the_consent_is_there()
    {
        _tokens.ConsentMissing = true;
        var tagId = (await CreateTagAsync("Later")).GetProperty("id").GetGuid();
        var id = await DeliverAsync("Read me");
        await ListedAsync(Inbox);
        await Json(await _client.PostAsJsonAsync("/api/tags", new { name = "Unused" }, Ct));
        // Dovecot keeps keywords; an Exchange server would not. Force what Exchange would decide.
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<MailDbContext>().Accounts.IgnoreQueryFilters().Where(a => a.Id == _account)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.TagStorage, TagStorage.Local), Ct);
        }

        await TagAsync(id, add: tagId);
        Assert.Empty(_graph.CategoriesOf("Read me"));
        Assert.Equal(TagStorage.Local, await StorageAsync());

        _tokens.ConsentMissing = false;
        await ScanAsync();

        Assert.Equal(TagStorage.Graph, await StorageAsync());
        Assert.Equal(["Later"], _graph.CategoriesOf("Read me"));
        Assert.Equal(["$later"], (await ListedAsync(Inbox))["Read me"]);
    }

    [Fact]
    public async Task Outlook_categories_are_offered_and_deleting_a_tag_takes_its_category_away()
    {
        var tagId = (await CreateTagAsync("Projects")).GetProperty("id").GetGuid();
        await TagAsync(await DeliverAsync("Plan"), add: tagId);
        _graph.Master["Green category"] = "preset4";

        var suggestions = (await Json(await _client.GetAsync("/api/tags/suggestions", Ct))).EnumerateArray().ToList();
        var green = suggestions.Single(s => s.GetProperty("name").GetString() == "Green category");
        Assert.True(green.GetProperty("outlook").GetBoolean());
        Assert.Equal("#13a10e", green.GetProperty("color").GetString());
        Assert.DoesNotContain(suggestions, s => s.GetProperty("name").GetString() == "Projects");

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/tags/{tagId}?fromMessages=true", Ct)).StatusCode);
        Assert.Empty(_graph.CategoriesOf("Plan"));
        Assert.False(_graph.Master.ContainsKey("Projects"));
    }

    [Fact]
    public async Task A_rule_sets_the_category()
    {
        var tagId = (await CreateTagAsync("Receipts")).GetProperty("id").GetGuid();
        await Json(await _client.PutAsJsonAsync($"{Base}/rules", new
        {
            rules = new[] { new { name = "Shop", enabled = true, matchAll = true, stop = false, conditions = new[] { new { field = "subject", op = "contains", value = "Receipt" } }, actions = new object[] { new { type = "tag", tagId } } } },
        }, Ct));

        await DeliverAsync("Receipt 42");
        await ListedAsync(Inbox);

        Assert.Equal(["Receipts"], _graph.CategoriesOf("Receipt 42"));
        Assert.Equal(["$receipts"], (await ListedAsync(Inbox))["Receipt 42"]);
    }

    private sealed class FakeGraphTokens : IGraphTokens
    {
        public bool ConsentMissing { get; set; }

        public Task<string?> TokenAsync(MailAccount account, CancellationToken ct) =>
            ConsentMissing ? throw new GraphConsentRequiredException() : Task.FromResult<string?>("graph-token");
    }

    /// <summary>Just enough of Graph: master categories and the categories of messages, by Message-ID.</summary>
    private sealed partial class FakeGraph : HttpMessageHandler
    {
        private sealed record Message(string Id, string InternetMessageId, string Subject)
        {
            public List<string> Categories { get; set; } = [];
        }

        private readonly ConcurrentDictionary<string, Message> _messages = new();

        /// <summary>Category name → color.</summary>
        public ConcurrentDictionary<string, string> Master { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool Down { get; set; }

        public void Add(string messageId, string subject, string[] categories)
        {
            var id = Guid.NewGuid().ToString("N");
            _messages[id] = new Message(id, $"<{messageId}>", subject) { Categories = [.. categories] };
        }

        public List<string> CategoriesOf(string subject) => _messages.Values.Single(m => m.Subject == subject).Categories;

        public void SetCategories(string subject, params string[] categories) =>
            _messages.Values.Single(m => m.Subject == subject).Categories = [.. categories];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Down)
            {
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            Assert.Equal("graph-token", request.Headers.Authorization?.Parameter);
            var path = request.RequestUri!.AbsolutePath.Replace("/v1.0", "", StringComparison.Ordinal);
            var body = request.Content is null ? default : JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct)).RootElement;
            if (path == "/me/outlook/masterCategories")
            {
                if (request.Method == HttpMethod.Post)
                {
                    var name = body.GetProperty("displayName").GetString()!;
                    Master[name] = body.GetProperty("color").GetString()!;
                    return Ok(Category(name));
                }

                return Ok(new { value = Master.Keys.Select(Category) });
            }

            if (path.StartsWith("/me/outlook/masterCategories/", StringComparison.Ordinal))
            {
                var name = Uri.UnescapeDataString(path["/me/outlook/masterCategories/".Length..]);
                if (request.Method == HttpMethod.Delete)
                {
                    Master.TryRemove(name, out _);
                }
                else
                {
                    Master[name] = body.GetProperty("color").GetString()!;
                }

                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (path.StartsWith("/me/messages/", StringComparison.Ordinal) && request.Method == HttpMethod.Patch)
            {
                _messages[path["/me/messages/".Length..]].Categories = body.GetProperty("categories").EnumerateArray().Select(c => c.GetString()!).ToList();
                return Ok(new { });
            }

            if (path == "/me/messages")
            {
                var filter = Uri.UnescapeDataString(Regex.Match(request.RequestUri.Query, @"\$filter=([^&]*)").Groups[1].Value);
                IEnumerable<Message> found = _messages.Values;
                if (InternetMessageId().Match(filter) is { Success: true } byId)
                {
                    found = found.Where(m => m.InternetMessageId == byId.Groups[1].Value.Replace("''", "'"));
                }
                else if (Category().Match(filter) is { Success: true } byCategory)
                {
                    var name = byCategory.Groups[1].Value.Replace("''", "'");
                    found = found.Where(m => m.Categories.Contains(name, StringComparer.OrdinalIgnoreCase));
                }

                return Ok(new
                {
                    value = found.Select(m => new
                    {
                        id = m.Id,
                        internetMessageId = m.InternetMessageId,
                        categories = m.Categories,
                        subject = m.Subject,
                        from = new { emailAddress = new { name = "Sender", address = "sender@example.test" } },
                        toRecipients = Array.Empty<object>(),
                        receivedDateTime = DateTimeOffset.UtcNow,
                        isRead = false,
                        hasAttachments = false,
                        bodyPreview = m.Subject,
                    }),
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        // The fake's category id is its name.
        private object Category(string name) => new { id = name, displayName = name, color = Master[name] };

        private static HttpResponseMessage Ok(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

        [GeneratedRegex(@"^internetMessageId eq '(.*)'$")]
        private static partial Regex InternetMessageId();

        [GeneratedRegex(@"^categories/any\(c:c eq '(.*)'\)$")]
        private static partial Regex Category();
    }
}
