using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SocklessMail.Api.Localization;

namespace SocklessMail.Tests;

/// <summary>
/// Server messages in the user's language (L.cs). The tests use the made-up languages "xq" and
/// "xq_XQ", so they cannot collide with real translations other tests may have loaded.
/// </summary>
public sealed class LocalizationTests : IDisposable
{
    private const string PortMessage = "Enter a port between 1 and 65535.";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sockless-mail-l10n-" + Guid.NewGuid().ToString("N"));

    public LocalizationTests()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "xq.json"), JsonSerializer.Serialize(new
        {
            translations = new Dictionary<string, object>
            {
                [PortMessage] = "Port 1–65535, xq.",
                ["Use at most {max} characters."] = "At most {max}, xq.",
                ["_{count} message_::_{count} messages_"] = new[] { "{count} one", "{count} many" },
                ["Choose a folder."] = "",
            },
            pluralForm = "nplurals=2; plural=(n != 1);",
        }));
        File.WriteAllText(Path.Combine(_directory, "xq_XQ.json"),
            """{ "translations": { "Choose a folder.": "Folder, formal xq." }, "pluralForm": "" }""");
        L.Load(_directory);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Parse_keeps_plain_translations_only()
    {
        var table = L.Parse(File.ReadAllText(Path.Combine(_directory, "xq.json")));

        Assert.Equal("Port 1–65535, xq.", table[PortMessage]);
        Assert.False(table.ContainsKey("_{count} message_::_{count} messages_"));
        Assert.False(table.ContainsKey("Choose a folder."));
    }

    [Fact]
    public async Task Messages_follow_the_language_with_fallbacks()
    {
        Assert.Equal(PortMessage, await In(null, () => L.T(PortMessage)));
        Assert.Equal("Port 1–65535, xq.", await In("xq", () => L.T(PortMessage)));
        // A regional code falls back to its language, and uses its own file where there is one.
        Assert.Equal("Port 1–65535, xq.", await In("xq_ZZ", () => L.T(PortMessage)));
        Assert.Equal("Folder, formal xq.", await In("xq_XQ", () => L.T("Choose a folder.")));
        // Untranslated or empty: English. Unknown or malformed languages: English.
        Assert.Equal("Choose a folder.", await In("xq", () => L.T("Choose a folder.")));
        Assert.Equal(PortMessage, await In("zz", () => L.T(PortMessage)));
        Assert.Equal(PortMessage, await In("../xq", () => L.T(PortMessage)));
    }

    [Fact]
    public async Task Placeholders_are_filled_in_the_translation()
    {
        Assert.Equal("At most 64, xq.", await In("xq", () => L.T("Use at most {max} characters.", ("max", 64))));
        Assert.Equal("Use at most 1000 characters.", await In(null, () => L.T("Use at most {max} characters.", ("max", 1000))));
        Assert.Equal("Name: ", L.T("Name: {name}", ("name", null)));
    }

    [Fact]
    public async Task The_language_ends_with_the_async_flow_that_set_it()
    {
        await In("xq", () => L.T(PortMessage));

        Assert.Equal(PortMessage, L.T(PortMessage));
    }

    [Fact]
    public void Request_language_prefers_the_header_and_falls_back_to_the_browser()
    {
        var request = new DefaultHttpContext().Request;
        request.Headers.AcceptLanguage = "sv-SE,sv;q=0.9,en;q=0.8";
        Assert.Equal("sv_SE", L.RequestLanguage(request));

        request.Headers[L.LanguageHeader] = "de_DE";
        Assert.Equal("de_DE", L.RequestLanguage(request));

        Assert.Null(L.RequestLanguage(new DefaultHttpContext().Request));
    }

    [Fact]
    public async Task Validation_errors_come_in_the_requested_language()
    {
        await using var app = new TestApp();
        var client = app.ClientAs("alice");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/accounts")
        {
            Content = JsonContent.Create(new
            {
                displayName = "x", emailAddress = "a@example.com", username = "a", password = "p",
                imap = new { host = "mail.example.com", port = 0, security = "sslTls" },
                smtp = new { host = "mail.example.com", port = 587, security = "startTls" },
            }),
        };
        request.Headers.Add("X-Sockless-Request", "1");
        request.Headers.Add(L.LanguageHeader, "xq");

        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("errors");
        Assert.Equal("Port 1–65535, xq.", errors.GetProperty("imap.port")[0].GetString());
    }

    /// <summary>Runs <paramref name="work"/> in its own async flow with <paramref name="language"/> set, as the middleware does.</summary>
    private static async Task<string> In(string? language, Func<string> work)
    {
        await Task.Yield();
        L.Use(language);
        return work();
    }
}
