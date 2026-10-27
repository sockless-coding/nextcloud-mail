using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using SocklessMail.Api;
using SocklessMail.Api.Compose;
using Microsoft.Extensions.Logging.Abstractions;
using static SocklessMail.Tests.AccountTestData;

namespace SocklessMail.Tests;

/// <summary>Uploads and the send/draft endpoints up to the point where a mail server would be needed.</summary>
public sealed class ComposeEndpointTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly HttpClient _alice;
    private readonly HttpClient _bob;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public ComposeEndpointTests()
    {
        _alice = _app.ClientAs("alice");
        _bob = _app.ClientAs("bob");
    }

    public void Dispose() => _app.Dispose();

    private async Task<string> UploadAsync(HttpClient client, string name = "notes.txt", byte[]? content = null)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content ?? "hello"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", name);
        var response = await client.PostAsync("/api/uploads", form, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await JsonAsync(response)).GetProperty("id").GetString()!;
    }

    private static object Message(string to = "bob@example.com", string[]? attachments = null) => new
    {
        to,
        subject = "Hi",
        text = "Hello",
        attachmentIds = attachments ?? [],
    };

    [Fact]
    public async Task Upload_returns_cleaned_metadata()
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent("x"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "../../evil.txt");

        var body = await JsonAsync(await _alice.PostAsync("/api/uploads", form, Ct));

        Assert.Matches("^[0-9a-f]{32}$", body.GetProperty("id").GetString());
        Assert.Equal("evil.txt", body.GetProperty("fileName").GetString());
        Assert.Equal(1, body.GetProperty("size").GetInt64());
    }

    [Fact]
    public async Task Upload_needs_the_csrf_header()
    {
        var client = _app.ClientAs("alice");
        client.DefaultRequestHeaders.Remove("X-Sockless-Request");
        using var form = new MultipartFormDataContent { { new ByteArrayContent([1]), "file", "a.bin" } };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/uploads", form, Ct)).StatusCode);
    }

    [Fact]
    public async Task Send_validates_before_contacting_the_server()
    {
        var accountId = await CreateAsync(_alice);

        foreach (var (to, field) in new[] { ("", "to"), ("not an address", "to"), ("undisclosed-recipients:;", "to") })
        {
            var response = await _alice.PostAsJsonAsync($"/api/accounts/{accountId}/send", Message(to), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True((await JsonAsync(response)).GetProperty("errors").TryGetProperty(field, out _), to);
        }
    }

    [Fact]
    public async Task Another_users_upload_cannot_be_attached()
    {
        var accountId = await CreateAsync(_alice);
        var bobsUpload = await UploadAsync(_bob);

        var response = await _alice.PostAsJsonAsync($"/api/accounts/{accountId}/send", Message(attachments: [bobsUpload]), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await JsonAsync(response)).GetProperty("errors").TryGetProperty("attachmentIds", out _));
    }

    [Fact]
    public async Task Another_user_cannot_delete_an_upload()
    {
        var upload = await UploadAsync(_alice);
        await _bob.DeleteAsync($"/api/uploads/{upload}", Ct);

        var store = _app.Services.GetRequiredService<AttachmentUploads>();
        Assert.NotNull(await store.GetAsync("alice", upload, Ct));
    }

    [Fact]
    public async Task Valid_message_goes_to_the_smtp_server()
    {
        var accountId = await CreateAsync(_alice);
        var upload = await UploadAsync(_alice);

        // The account points at 127.0.0.1, which the address policy refuses: proof the send
        // got past validation and the attachment lookup to the SMTP connection.
        var response = await _alice.PostAsJsonAsync($"/api/accounts/{accountId}/send", Message("Bob <bob@example.com>, carol@example.com", [upload]), Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("address_not_allowed", (await JsonAsync(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Draft_needs_a_known_mode()
    {
        var accountId = await CreateAsync(_alice);
        var response = await _alice.GetAsync($"/api/accounts/{accountId}/folders/SU5CT1g/messages/1-1/draft?mode=sideways", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "folders/SU5CT1g/messages/1-1/draft?mode=reply", null)]
    [InlineData("POST", "send", """{"to":"x@example.com","subject":"s","text":"t"}""")]
    public async Task Other_users_account_cannot_send_or_draft(string method, string path, string? body)
    {
        var aliceId = await CreateAsync(_alice);
        var request = new HttpRequestMessage(new HttpMethod(method), $"/api/accounts/{aliceId}/{path}");
        if (body is not null)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        }

        Assert.Equal(HttpStatusCode.NotFound, (await _bob.SendAsync(request, Ct)).StatusCode);
    }

    [Fact]
    public async Task Uploads_expire_and_are_cleaned_up()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var options = _app.Services.GetRequiredService<SocklessMailOptions>();
        var store = new AttachmentUploads(options, clock, NullLogger<AttachmentUploads>.Instance);
        var upload = (await store.SaveAsync("carol", "a.txt", "text/plain", new MemoryStream([1, 2, 3]), Ct))!;

        Assert.NotNull(await store.GetAsync("carol", upload.Id, Ct));
        clock.Advance(AttachmentUploads.Lifetime + TimeSpan.FromMinutes(1));
        Assert.Null(await store.GetAsync("carol", upload.Id, Ct));
    }

    [Theory]
    [InlineData("../../keys/key")]
    [InlineData("0123")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789")]
    public async Task Malformed_upload_ids_are_not_looked_up(string id)
    {
        var store = _app.Services.GetRequiredService<AttachmentUploads>();
        Assert.Null(await store.GetAsync("alice", id, Ct));
    }

    [Fact]
    public async Task Oversized_upload_is_refused()
    {
        var options = _app.Services.GetRequiredService<SocklessMailOptions>();
        var store = new AttachmentUploads(options, TimeProvider.System, NullLogger<AttachmentUploads>.Instance);
        var big = new MemoryStream(new byte[AttachmentUploads.MaxFileBytes + 1]);
        Assert.Null(await store.SaveAsync("alice", "big.bin", "application/octet-stream", big, Ct));
    }
}
