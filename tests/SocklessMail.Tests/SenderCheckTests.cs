using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SocklessMail.Api.Senders;

namespace SocklessMail.Tests;

public sealed class SkeletonTests
{
    [Theory]
    [InlineData("pаypal", "paypal")] // Cyrillic а
    [InlineData("раураl", "paypal")] // Cyrillic р, а, у
    [InlineData("paypa1", "paypal")]
    [InlineData("PAYPAL", "paypal")]
    [InlineData("páypal", "paypal")]
    [InlineData("rnicrosoft", "microsoft")]
    [InlineData("g00gle", "google")]
    [InlineData("vvikipedia", "wikipedia")]
    public void Look_alikes_share_a_skeleton(string lookalike, string original)
    {
        Assert.Equal(Skeletons.Of(original), Skeletons.Of(lookalike));
    }

    [Theory]
    [InlineData("paypal", "paypa")]
    [InlineData("google", "gogle")]
    public void Different_names_do_not(string a, string b)
    {
        Assert.NotEqual(Skeletons.Of(a), Skeletons.Of(b));
    }

    [Theory]
    [InlineData("pаypal", true)]
    [InlineData("müller", false)]
    [InlineData("пример", false)]
    [InlineData("ελληνικά", false)]
    [InlineData("shop-24", false)]
    [InlineData("αpple", true)]
    public void Mixed_scripts(string label, bool mixed)
    {
        Assert.Equal(mixed, Skeletons.MixesScripts(label));
    }

    [Theory]
    [InlineData("paypal", "paypai", true)]
    [InlineData("microsoft", "micorsoft", true)]
    [InlineData("microsoft", "microsof", true)]
    [InlineData("microsoft", "microsofts", true)]
    [InlineData("microsoft", "microsoft", false)]
    [InlineData("microsoft", "mircosfot", false)]
    [InlineData("paypal", "paypal-x", false)]
    public void One_edit_apart(string a, string b, bool expected)
    {
        Assert.Equal(expected, SenderChecker.OneEditApart(a, b));
        Assert.Equal(expected, SenderChecker.OneEditApart(b, a));
    }
}

public sealed class SenderDomainTests
{
    [Theory]
    [InlineData("mail.paypal.co.uk", "paypal", "co.uk", "mail")]
    [InlineData("paypal.com", "paypal", "com", "")]
    [InlineData("a.b.example.se", "example", "se", "a.b")]
    [InlineData("example.co", "example", "co", "")]
    [InlineData("hmrc.gov.uk", "hmrc", "gov.uk", "")]
    public void Splits_registered_domains(string name, string label, string suffix, string subdomain)
    {
        var domain = SenderDomain.Parse(name)!;
        Assert.Equal((label, suffix, subdomain), (domain.Label, domain.Suffix, domain.Subdomain));
    }

    [Fact]
    public void International_names_read_in_unicode()
    {
        var domain = SenderDomain.OfAddress("x@pаypal.com")!;
        Assert.Equal("xn--pypal-4ve.com", domain.Name);
        Assert.Equal("pаypal.com", domain.Unicode);
        Assert.True(domain.IsInternational);
    }

    [Fact]
    public void The_brand_list_loads()
    {
        Assert.Contains(SenderIndex.Brands, b => b.Label == "paypal" && b.Suffix is null);
        Assert.Contains(SenderIndex.Brands, b => b is { Label: "orange", CommonWord: true });
        Assert.Contains(SenderIndex.Brands, b => b is { Label: "hmrc", Suffix: "gov.uk" });
    }
}

public sealed class RdapTests
{
    [Fact]
    public void The_bootstrap_file_maps_endings_to_https_servers()
    {
        var servers = DomainAges.ParseBootstrap(JsonDocument.Parse("""
            {"services": [
              [["com", "NET"], ["http://rdap.example/com/", "https://rdap.verisign.example/com/v1"]],
              [["se"], ["http://only-plain.example/"]],
              "garbage",
              [["xn--p1ai"], ["https://rdap.ru.example/"]]
            ]}
            """).RootElement);

        Assert.Equal("https://rdap.verisign.example/com/v1/", servers["com"].ToString());
        Assert.Equal("https://rdap.verisign.example/com/v1/", servers["net"].ToString());
        Assert.False(servers.ContainsKey("se"));
        Assert.Equal("https://rdap.ru.example/", servers["xn--p1ai"].ToString());
    }

    [Fact]
    public void The_registration_event_dates_a_domain()
    {
        Assert.Equal(new DateTimeOffset(1997, 9, 15, 4, 0, 0, TimeSpan.Zero), DomainAges.RdapRegistration(JsonDocument.Parse("""
            {"events": [
              {"eventAction": "expiration", "eventDate": "2028-09-14T04:00:00Z"},
              {"eventAction": "registration", "eventDate": "1997-09-15T04:00:00Z"}
            ]}
            """).RootElement));
        Assert.Null(DomainAges.RdapRegistration(JsonDocument.Parse("""{"events": [{"eventAction": "last changed", "eventDate": "2020-01-01T00:00:00Z"}]}""").RootElement));
        Assert.Null(DomainAges.RdapRegistration(JsonDocument.Parse("""{"errorCode": 404}""").RootElement));
    }
}

public sealed class SenderCheckerTests
{
    private static readonly SenderIndex Index = new(
        [
            .. SenderIndex.Brands,
            KnownDomain.Of(SenderDomain.Parse("sockless.se")!, KnownSource.Account),
            KnownDomain.Of(SenderDomain.Parse("klubben.example")!, KnownSource.Contact),
        ],
        ["trusted-partner.com"]);

    private static SenderReason Single(SenderWarning? warning) => Assert.Single(Assert.IsType<SenderWarning>(warning).Reasons);

    [Theory]
    [InlineData("x@pаypal.com", "paypal.com", "alphabet")]
    [InlineData("x@аррӏе.com", "apple.com", "alphabet")]
    [InlineData("x@paypa1.com", "paypal.com", "characters")]
    [InlineData("x@rnicrosoft.com", "microsoft.com", "characters")]
    [InlineData("x@arnazon.de", "amazon.de", "characters")]
    [InlineData("x@sock1ess.se", "sockless.se", "characters")]
    [InlineData("x@micorsoft.com", "microsoft.com", "typo")]
    [InlineData("x@paypai.com", "paypal.com", "typo")]
    [InlineData("x@sockiess.se", "sockless.se", "typo")]
    [InlineData("x@klubbem.example", "klubben.example", "typo")]
    [InlineData("x@dropbox.net", "dropbox.com", "ending")]
    [InlineData("x@swedbank.nu", "swedbank.se", "ending")]
    public void Look_alike_domains(string address, string looksLike, string detail)
    {
        var reason = Single(SenderChecker.Check(Index, null, address));
        Assert.Equal(("lookalike", looksLike, detail), (reason.Code, reason.LooksLike, reason.Detail));
    }

    [Fact]
    public void Look_alike_characters_are_dangerous_typos_a_warning()
    {
        Assert.Equal("danger", SenderChecker.Check(Index, null, "x@pаypal.com")!.Level);
        Assert.Equal("warning", SenderChecker.Check(Index, null, "x@micorsoft.com")!.Level);
    }

    [Fact]
    public void The_warning_names_both_spellings_of_an_international_domain()
    {
        var warning = SenderChecker.Check(Index, null, "x@pаypal.com")!;
        Assert.Equal("pаypal.com", warning.Domain);
        Assert.Equal("xn--pypal-4ve.com", warning.AsciiDomain);
        Assert.Null(SenderChecker.Check(Index, null, "x@micorsoft.com")!.AsciiDomain);
    }

    [Theory]
    [InlineData("x@apple-support-center.com", "apple.com")]
    [InlineData("x@paypal.com.secure-login.top", "paypal.com")]
    [InlineData("x@login-swedbank.net", "swedbank.se")]
    public void Domains_carrying_a_brand_name(string address, string brand)
    {
        var reason = Single(SenderChecker.Check(Index, null, address));
        Assert.Equal(("brandName", brand), (reason.Code, reason.LooksLike));
    }

    [Theory]
    [InlineData("x@paypal.com")]
    [InlineData("service@mail.paypal.com")]
    [InlineData("x@paypal.de")]
    [InlineData("x@amazon.co.uk")]
    [InlineData("x@sockless.se")]
    [InlineData("x@orange-county.org")] // a common word, not a look-alike
    [InlineData("x@example.org")]
    [InlineData("x@müller.de")]
    [InlineData("x@seb.com")] // too short to say
    [InlineData("x@trusted-partner.com")]
    [InlineData("x@mail.trusted-partner.com")]
    [InlineData("not an address")]
    public void Ordinary_senders(string address)
    {
        Assert.Null(SenderChecker.Check(Index, null, address));
    }

    [Fact]
    public void Letters_from_several_alphabets()
    {
        var reason = Single(SenderChecker.Check(Index, null, "x@exаmple-shop.org"));
        Assert.Equal("mixedScripts", reason.Code);
    }

    [Theory]
    [InlineData("service@paypal.com", "paypal.com")]
    [InlineData("PayPal.com Support", "paypal.com")]
    [InlineData("Kundservice (swedbank.se)", "swedbank.se")]
    public void Names_that_show_another_domain(string name, string shown)
    {
        var reason = Single(SenderChecker.Check(Index, name, "x@example.net"));
        Assert.Equal(("nameShowsDomain", shown, "example.net"), (reason.Code, reason.LooksLike, reason.Domain));
    }

    [Theory]
    [InlineData("Amazon.de")]
    [InlineData("J. R. Smith")]
    [InlineData("Mr.Smith")]
    [InlineData("Version 2.1")]
    [InlineData("noreply@news.example.net")]
    public void Names_that_are_fine(string name)
    {
        Assert.Null(SenderChecker.Check(Index, name, name == "Amazon.de" ? "x@amazon.de" : "x@example.net"));
    }

    [Fact]
    public void A_failed_dmarc_check_is_dangerous()
    {
        var warning = SenderChecker.Check(Index, null, "x@paypal.com", null, dmarcFailed: true)!;
        Assert.Equal("danger", warning.Level);
        Assert.Equal("dmarcFail", Assert.Single(warning.Reasons).Code);
    }

    [Fact]
    public void Reply_to_is_checked_for_look_alikes_and_otherwise_only_mentioned()
    {
        var reason = Single(SenderChecker.Check(Index, null, "x@example.net", "billing@paypa1.com", false));
        Assert.Equal(("lookalike", "replyTo", "paypal.com"), (reason.Code, reason.Field, reason.LooksLike));

        // A different Reply-To alone is common (newsletters, ticket systems).
        Assert.Null(SenderChecker.Check(Index, null, "x@example.net", "y@example.org", false));
        var warning = SenderChecker.Check(Index, null, "x@micorsoft.com", "y@example.org", false)!;
        Assert.Equal(["lookalike", "replyTo"], warning.Reasons.Select(r => r.Code));
    }

    [Theory]
    [InlineData(20, "warning")]
    [InlineData(6, "danger")]
    [InlineData(0, "danger")]
    public void Newly_registered_domains(int days, string level)
    {
        var warning = SenderChecker.Check(Index, null, "x@mail.fresh-shop.com", days)!;
        Assert.Equal(level, warning.Level);
        var reason = Assert.Single(warning.Reasons);
        Assert.Equal(("newDomain", "fresh-shop.com", days.ToString()), (reason.Code, reason.Domain, reason.Detail));
    }

    [Theory]
    [InlineData("x@fresh-shop.com", 30)]
    [InlineData("x@fresh-shop.com", null)]
    [InlineData("x@sockless.se", 2)] // the user's own domain, however new
    [InlineData("x@trusted-partner.com", 2)]
    public void Old_unknown_known_and_trusted_domains_are_not(string address, int? days)
    {
        Assert.Null(SenderChecker.Check(Index, null, address, days));
    }

    [Fact]
    public void A_new_domain_adds_to_other_reasons()
    {
        var warning = SenderChecker.Check(Index, null, "x@micorsoft.com", 12)!;
        Assert.Equal(["lookalike", "newDomain"], warning.Reasons.Select(r => r.Code));
        Assert.Equal("warning", warning.Level);
    }

    [Fact]
    public void Trusted_domains_are_never_flagged_and_their_look_alikes_are()
    {
        Assert.Null(SenderChecker.Check(Index, "service@paypal.com", "x@trusted-partner.com", null, true));
        var reason = Single(SenderChecker.Check(Index, null, "x@trusted-partmer.com"));
        Assert.Equal(("lookalike", "trusted-partner.com"), (reason.Code, reason.LooksLike));
    }
}

/// <summary>The endpoints, with contacts from a fake CardDAV server and trust stored per user.</summary>
public sealed class SenderEndpointTests : IDisposable
{
    private readonly TestApp _app = new();
    private readonly FakeCardDav _carddav = new();
    private readonly HttpClient _alice;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public SenderEndpointTests()
    {
        _app.Nextcloud.Respond = _carddav.Respond;
        _alice = _app.ClientAs("alice");
        _carddav.AddBook("alice", "contacts", "Contacts");
        _carddav.AddCard("alice", "contacts", "petra.vcf", "BEGIN:VCARD\nVERSION:3.0\nFN:Petra\nEMAIL:petra@probe-industries.example\nEND:VCARD");
    }

    public void Dispose() => _app.Dispose();

    private async Task<JsonElement[]> CheckAsync(HttpClient client, params object[] senders)
    {
        var response = await client.PostAsJsonAsync("/api/sender-checks", new { senders }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("warnings").EnumerateArray()];
    }

    [Fact]
    public async Task Senders_are_checked_against_brands_and_the_users_contacts()
    {
        var warnings = await CheckAsync(_alice,
            new { name = "PayPal", address = "service@pаypal.com" },
            new { name = "Petra", address = "petra@probe-industries.example" },
            new { name = "Petra", address = "petra@prabe-industries.example" },
            new { name = (string?)null, address = "news@example.org" });

        Assert.Equal("paypal.com", warnings[0].GetProperty("reasons")[0].GetProperty("looksLike").GetString());
        Assert.Equal("danger", warnings[0].GetProperty("level").GetString());
        Assert.Equal(JsonValueKind.Null, warnings[1].ValueKind);
        Assert.Equal("probe-industries.example", warnings[2].GetProperty("reasons")[0].GetProperty("looksLike").GetString());
        Assert.Equal(JsonValueKind.Null, warnings[3].ValueKind);

        // Bob's contacts are his own: the same look-alike means nothing to him.
        Assert.Equal(JsonValueKind.Null, (await CheckAsync(_app.ClientAs("bob"), new { address = "petra@prabe-industries.example" }))[0].ValueKind);
    }

    [Fact]
    public async Task Trusting_a_domain_silences_it_for_that_user_only()
    {
        Assert.NotEqual(JsonValueKind.Null, (await CheckAsync(_alice, new { address = "x@micorsoft.com" }))[0].ValueKind);

        var trust = await _alice.PostAsJsonAsync("/api/trusted-senders", new { domain = "mail.micorsoft.com" }, Ct);
        Assert.Equal(HttpStatusCode.OK, trust.StatusCode);
        Assert.Equal("micorsoft.com", JsonDocument.Parse(await trust.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("domain").GetString());
        Assert.Equal(["micorsoft.com"], (await _alice.GetFromJsonAsync<string[]>("/api/trusted-senders", Ct))!);
        Assert.Equal(JsonValueKind.Null, (await CheckAsync(_alice, new { address = "x@micorsoft.com" }))[0].ValueKind);

        var bob = _app.ClientAs("bob");
        Assert.Empty((await bob.GetFromJsonAsync<string[]>("/api/trusted-senders", Ct))!);
        Assert.NotEqual(JsonValueKind.Null, (await CheckAsync(bob, new { address = "x@micorsoft.com" }))[0].ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync("/api/trusted-senders/micorsoft.com", Ct)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _alice.DeleteAsync("/api/trusted-senders/micorsoft.com", Ct)).StatusCode);
        Assert.NotEqual(JsonValueKind.Null, (await CheckAsync(_alice, new { address = "x@micorsoft.com" }))[0].ValueKind);
    }

    private async Task<JsonElement> CheckWithPendingAsync(params object[] senders)
    {
        var response = await _alice.PostAsJsonAsync("/api/sender-checks", new { senders }, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
    }

    /// <summary>Asks until no lookup is pending, as the browser does.</summary>
    private async Task<JsonElement> CheckUntilSettledAsync(params object[] senders)
    {
        for (var attempt = 0; ; attempt++)
        {
            var result = await CheckWithPendingAsync(senders);
            if (result.GetProperty("pending").GetArrayLength() == 0 || attempt == 50)
            {
                return result;
            }

            await Task.Delay(100, Ct);
        }
    }

    [Fact]
    public async Task Domain_ages_are_looked_up_in_the_background()
    {
        _app.Rdap.Registered["fresh-shop.com"] = DateTimeOffset.UtcNow.AddDays(-3);
        _app.Rdap.Registered["old-shop.com"] = new DateTimeOffset(2001, 5, 1, 0, 0, 0, TimeSpan.Zero);
        object[] senders =
        [
            new { address = "sales@fresh-shop.com" },
            new { address = "sales@old-shop.com" },
            new { address = "x@nowhere.example" },
            new { name = "Petra", address = "petra@probe-industries.example" },
        ];

        // Nothing waits for a registry: the first answer comes at once and says what may change.
        var first = await CheckWithPendingAsync(senders);
        Assert.Equal([0, 1, 2], first.GetProperty("pending").EnumerateArray().Select(p => p.GetInt32()));
        Assert.Equal(JsonValueKind.Null, first.GetProperty("warnings")[0].ValueKind);

        var settled = await CheckUntilSettledAsync(senders);
        Assert.Equal(0, settled.GetProperty("pending").GetArrayLength());
        var warnings = settled.GetProperty("warnings");
        var reason = warnings[0].GetProperty("reasons")[0];
        Assert.Equal(("newDomain", "3", "danger"), (reason.GetProperty("code").GetString(), reason.GetProperty("detail").GetString(), warnings[0].GetProperty("level").GetString()));
        Assert.Equal(JsonValueKind.Null, warnings[1].ValueKind);
        Assert.Equal(JsonValueKind.Null, warnings[2].ValueKind);
        Assert.Equal(JsonValueKind.Null, warnings[3].ValueKind);

        // Contacts' domains are never asked about; the bootstrap file is fetched once.
        Assert.DoesNotContain(_app.Rdap.Requests, url => url.Contains("probe-industries"));
        Assert.Single(_app.Rdap.Requests, url => url == DomainAges.BootstrapUrl);
        Assert.Single(_app.Rdap.Requests, url => url.EndsWith("/domain/fresh-shop.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failing_registry_never_flags_a_sender()
    {
        _app.Rdap.Registered["fresh-shop.com"] = DateTimeOffset.UtcNow.AddDays(-3);
        _app.Rdap.Status = HttpStatusCode.TooManyRequests;
        var settled = await CheckUntilSettledAsync(new { address = "sales@fresh-shop.com" });
        Assert.Equal(0, settled.GetProperty("pending").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, settled.GetProperty("warnings")[0].ValueKind);
    }

    [Fact]
    public async Task Admins_can_turn_the_lookups_off()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _alice.PutAsJsonAsync("/api/admin/settings", new { domainAgeChecks = false }, Ct)).StatusCode);

        _app.Nextcloud.Responses["cloud/user"] = (HttpStatusCode.OK, """{"ocs":{"meta":{"status":"ok"},"data":{"id":"root","groups":["admin","users"]}}}""");
        var root = _app.ClientAs("root");
        Assert.True((await root.GetFromJsonAsync<JsonElement>("/api/admin/settings", Ct)).GetProperty("domainAgeChecks").GetBoolean());
        var saved = await root.PutAsJsonAsync("/api/admin/settings", new { domainAgeChecks = false }, Ct);
        Assert.False((await saved.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("domainAgeChecks").GetBoolean());
        Assert.False((await root.GetFromJsonAsync<JsonElement>("/api/admin/settings", Ct)).GetProperty("domainAgeChecks").GetBoolean());

        _app.Rdap.Registered["fresh-shop.com"] = DateTimeOffset.UtcNow.AddDays(-3);
        var result = await CheckWithPendingAsync(new { address = "sales@fresh-shop.com" });
        Assert.Equal(0, result.GetProperty("pending").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("warnings")[0].ValueKind);
        Assert.Empty(_app.Rdap.Requests);
    }

    [Fact]
    public async Task Bad_input_is_refused()
    {
        var tooMany = Enumerable.Range(0, SenderChecks.MaxSenders + 1).Select(i => new { address = $"x{i}@example.org" }).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await _alice.PostAsJsonAsync("/api/sender-checks", new { senders = tooMany }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _alice.PostAsJsonAsync("/api/trusted-senders", new { domain = "not a domain" }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Contacts_being_unavailable_does_not_fail_the_check()
    {
        _app.Nextcloud.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var warnings = await CheckAsync(_alice, new { address = "x@pаypal.com" });
        Assert.Equal("lookalike", warnings[0].GetProperty("reasons")[0].GetProperty("code").GetString());
    }
}
