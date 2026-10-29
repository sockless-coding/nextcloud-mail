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
