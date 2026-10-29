using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SocklessMail.Api.AppApi;
using SocklessMail.Api.Files;

namespace SocklessMail.Tests;

/// <summary>Attaching from Files, share links, saving into Files and the Files app action, against a fake Nextcloud.</summary>
public sealed class FilesTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly FakeDav _dav = new();
    private readonly HttpClient _alice;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public FilesTests()
    {
        _app.Nextcloud.Respond = _dav.Respond;
        _alice = _app.ClientAs("alice");
    }

    public void Dispose() => _app.Dispose();

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private async Task<JsonElement[]> AttachAsync(object input)
    {
        var response = await _alice.PostAsJsonAsync("/api/files/attachments", input, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. (await JsonAsync(response)).GetProperty("files").EnumerateArray()];
    }

    [Fact]
    public async Task Attaching_by_path_copies_the_file_into_an_upload()
    {
        _dav.AddFolder("alice", "Documents");
        _dav.AddFile("alice", "Documents/Q3 report.txt", "numbers", "text/plain");

        var file = Assert.Single(await AttachAsync(new { paths = new[] { "/Documents/Q3 report.txt" } }));

        var upload = file.GetProperty("upload");
        Assert.Equal("Q3 report.txt", upload.GetProperty("fileName").GetString());
        Assert.Equal("text/plain", upload.GetProperty("contentType").GetString());
        Assert.Equal(7, upload.GetProperty("size").GetInt64());
        Assert.Equal(JsonValueKind.Null, file.GetProperty("error").ValueKind);

        // Read as alice, through AppAPI's header (not with a password).
        var get = Assert.Single(_app.Nextcloud.Requests, r => r.Method == HttpMethod.Get);
        Assert.Contains(_app.Nextcloud.Requests, r => r.Method.Method == "PROPFIND" && r.Headers["Depth"] == "0");
        Assert.Equal("/remote.php/dav/files/alice/Documents/Q3%20report.txt", get.PathAndQuery);
    }

    [Fact]
    public async Task A_folder_is_not_attached_as_a_web_page()
    {
        _dav.AddFolder("alice", "Photos");

        var file = Assert.Single(await AttachAsync(new { paths = new[] { "/Photos" } }));

        Assert.Equal("is_folder", file.GetProperty("error").GetString());
        Assert.DoesNotContain(_app.Nextcloud.Requests, r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task Attaching_by_file_id_finds_the_files_first()
    {
        var id = _dav.AddFile("alice", "a.txt", "A");
        var folder = _dav.AddFolder("alice", "Photos");

        var files = await AttachAsync(new { fileIds = new[] { id, folder, 999L } });

        Assert.Equal("a.txt", files.Single(f => f.GetProperty("upload").ValueKind != JsonValueKind.Null).GetProperty("name").GetString());
        Assert.Equal("is_folder", files.Single(f => f.GetProperty("name").GetString() == "Photos").GetProperty("error").GetString());
        Assert.Equal("not_found", files.Single(f => f.GetProperty("name").GetString() == "#999").GetProperty("error").GetString());
        var search = Assert.Single(_app.Nextcloud.Requests, r => r.Method.Method == "SEARCH");
        Assert.Contains("<d:href>/files/alice</d:href>", search.Body);
    }

    [Fact]
    public async Task Another_users_files_are_not_found()
    {
        var id = _dav.AddFile("bob", "secret.txt", "B");

        var files = await AttachAsync(new { fileIds = new[] { id }, paths = new[] { "/secret.txt" } });

        Assert.All(files, f => Assert.Equal("not_found", f.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Attaching_reports_each_failure_without_failing_the_rest()
    {
        _dav.AddFile("alice", "ok.txt", "fine");
        _dav.AddFile("alice", "big.bin", "");
        var dav = _app.Nextcloud.Respond!;
        _app.Nextcloud.Respond = r => r.Method == HttpMethod.Get && r.PathAndQuery.EndsWith("/big.bin", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                // Nextcloud announces the size, so nothing is downloaded.
                Content = new ByteArrayContent([1]) { Headers = { ContentLength = 30L * 1024 * 1024 } },
            }
            : dav(r);

        var files = await AttachAsync(new { paths = new[] { "/ok.txt", "/missing.txt", "/big.bin", "/../etc/passwd" } });

        Assert.Equal(["ok.txt", "missing.txt", "big.bin", "/../etc/passwd"], files.Select(f => f.GetProperty("name").GetString()));
        Assert.Equal([null, "not_found", "too_large", "not_found"], files.Select(f => f.GetProperty("error").GetString()));
        Assert.DoesNotContain(_app.Nextcloud.Requests, r => r.PathAndQuery.Contains("etc", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Attaching_needs_between_one_and_25_files()
    {
        foreach (var input in new object[] { new { paths = Array.Empty<string>() }, new { paths = Enumerable.Repeat("/a", 26).ToArray() } })
        {
            var response = await _alice.PostAsJsonAsync("/api/files/attachments", input, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task A_share_link_is_a_public_read_only_link_created_as_the_user()
    {
        _app.Nextcloud.Responses["files_sharing/api/v1/shares"] =
            (HttpStatusCode.OK, """{"ocs":{"meta":{"status":"ok"},"data":{"id":"7","token":"AbC","url":"http://nextcloud.internal/s/AbC"}}}""");

        var body = await JsonAsync(await _alice.PostAsJsonAsync("/api/files/share-links", new { path = "Videos/talk.mp4" }, Ct));

        // The browser builds the link from the token: the url has the host the app reaches Nextcloud by.
        Assert.Equal("AbC", body.GetProperty("token").GetString());
        Assert.Equal("talk.mp4", body.GetProperty("name").GetString());
        var request = Assert.Single(_app.Nextcloud.Requests);
        var sent = JsonDocument.Parse(request.Body).RootElement;
        Assert.Equal("/Videos/talk.mp4", sent.GetProperty("path").GetString());
        Assert.Equal(3, sent.GetProperty("shareType").GetInt32());
        Assert.Equal(1, sent.GetProperty("permissions").GetInt32());
        Assert.Equal("alice", AuthorizedUser(request));
    }

    [Fact]
    public async Task A_refused_share_link_says_why()
    {
        _app.Nextcloud.Responses["files_sharing/api/v1/shares"] = (HttpStatusCode.Forbidden, "{}");

        var response = await _alice.PostAsJsonAsync("/api/files/share-links", new { path = "/a.txt" }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("share_refused", (await JsonAsync(response)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Uploading_never_replaces_a_file()
    {
        _dav.AddFolder("alice", "Mail");
        _dav.AddFile("alice", "Mail/invoice.pdf", "old");
        _dav.AddFile("alice", "Mail/invoice (2).pdf", "older");
        // Someone takes "invoice (3).pdf" between our listing and our upload.
        _dav.TakenOnPut["alice/Mail/invoice (3).pdf"] = true;

        using var scope = _app.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<NextcloudFiles>();
        using var content = new MemoryStream("new"u8.ToArray());
        var path = await files.UploadAsync("alice", "/Mail", "invoice.pdf", "application/pdf", content, Ct);

        Assert.Equal("/Mail/invoice (4).pdf", path);
        Assert.Equal("new", _dav.Read("alice", path));
        Assert.Equal("old", _dav.Read("alice", "Mail/invoice.pdf"));
        Assert.All(_app.Nextcloud.Requests.Where(r => r.Method == HttpMethod.Put), r => Assert.Equal("*", r.Headers["If-None-Match"]));
    }

    [Theory]
    [InlineData(null, HttpStatusCode.NotFound, "folder_not_found")]
    [InlineData(HttpStatusCode.InsufficientStorage, HttpStatusCode.InsufficientStorage, "quota")]
    [InlineData(HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, "no_permission")]
    public async Task Uploading_explains_folder_problems(HttpStatusCode? putStatus, HttpStatusCode expected, string code)
    {
        if (putStatus is not null)
        {
            _dav.AddFolder("alice", "Mail");
            _dav.PutStatus = putStatus;
        }

        using var scope = _app.Services.CreateScope();
        var files = scope.ServiceProvider.GetRequiredService<NextcloudFiles>();
        using var content = new MemoryStream([1]);
        var e = await Assert.ThrowsAsync<NextcloudFilesException>(() => files.UploadAsync("alice", "/Mail", "a.bin", "application/octet-stream", content, Ct));

        Assert.Equal(code, e.Code);
        Assert.Equal((int)expected, e.StatusCode);
    }

    [Fact]
    public async Task The_files_action_opens_the_mail_page_without_doing_anything_else()
    {
        // Posted by the Files app through the PHP proxy: no CSRF header of ours.
        var client = _app.ClientAs("alice");
        client.DefaultRequestHeaders.Remove("X-Sockless-Request");

        var response = await client.PostAsJsonAsync("/files-action/send", new { files = new[] { new { fileId = 5 } } }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("sockless_mail", (await JsonAsync(response)).GetProperty("redirect_handler").GetString());
        Assert.Empty(_app.Nextcloud.Requests);
    }

    [Fact]
    public async Task The_files_action_needs_a_user()
    {
        var response = await _app.CreateClient().PostAsync("/files-action/send", null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/Documents/a.pdf", new[] { "Documents", "a.pdf" })]
    [InlineData("Documents//a.pdf/", new[] { "Documents", "a.pdf" })]
    [InlineData("/", new string[0])]
    [InlineData("/a/../b", null)]
    [InlineData("/a/./b", null)]
    [InlineData("a\\b", null)]
    [InlineData("a\nb", null)]
    public void Paths_are_split_into_safe_names(string path, string[]? expected) =>
        Assert.Equal(expected, NextcloudFiles.SplitPath(path));

    [Theory]
    [InlineData("a.pdf", "a (2).pdf")]
    [InlineData("README", "README (2)")]
    [InlineData(".env", ".env (2)")]
    [InlineData("archive.tar.gz", "archive.tar (2).gz")]
    public void Taken_names_get_a_number(string name, string expected) =>
        Assert.Equal(expected, NextcloudFiles.FreeName(name, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name, name.ToUpperInvariant() }));

    private static string AuthorizedUser(RecordedRequest request)
    {
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers[AppApiHeaders.Authorization]));
        return decoded[..decoded.IndexOf(':')];
    }
}
