using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using SocklessMail.Api;
using SocklessMail.Api.Bimi;

namespace SocklessMail.Tests;

public sealed class BimiRecordsTests
{
    [Fact]
    public void TreeWalkRemovesOneLabelPerStep()
    {
        Assert.Equal(["a.b.example.com", "b.example.com", "example.com", "com"], BimiRecords.TreeWalk("a.b.example.com"));
    }

    [Fact]
    public void TreeWalkOfLongNamesJumpsToSevenLabels()
    {
        var walk = BimiRecords.TreeWalk("1.2.3.4.5.6.7.8.9.example.com");
        Assert.Equal(8, walk.Count);
        Assert.Equal("1.2.3.4.5.6.7.8.9.example.com", walk[0]);
        Assert.Equal("5.6.7.8.9.example.com", walk[1]);
        Assert.Equal("com", walk[^1]);
    }

    [Theory]
    [InlineData("v=DMARC1; p=reject", true, true)]
    [InlineData("v=DMARC1; p=quarantine; pct=100", true, true)]
    [InlineData("v=DMARC1; p=quarantine; pct=50", true, false)]
    [InlineData("v=DMARC1; p=none", true, false)]
    [InlineData("v=DMARC1; p=reject; t=y", true, false)]
    [InlineData("v=DMARC1; p=reject; sp=none", false, false)]
    [InlineData("v=DMARC1; p=none; sp=reject", false, true)]
    [InlineData("v=DMARC1; p=reject", false, true)]
    public void DmarcEnforcement(string txt, bool atAuthorDomain, bool enforced)
    {
        Assert.Equal(enforced, BimiRecords.ParseDmarc([txt])!.Enforces(atAuthorDomain));
    }

    [Fact]
    public void DmarcNeedsExactlyOneRecord()
    {
        Assert.Null(BimiRecords.ParseDmarc(["v=spf1 -all"]));
        Assert.Null(BimiRecords.ParseDmarc(["p=reject; v=DMARC1"]));
        Assert.Null(BimiRecords.ParseDmarc(["v=DMARC1; p=reject", "v=DMARC1; p=none"]));
        Assert.True(BimiRecords.ParseDmarc(["v=spf1 -all", "v=DMARC1; p=reject; psd=y"])!.PublicSuffix);
    }

    [Fact]
    public void BimiRecordLocation()
    {
        Assert.Equal("https://example.com/logo.svg", BimiRecords.ParseBimi(["v=BIMI1; l=https://example.com/logo.svg; a=https://example.com/vmc.pem"])!.Location);
        Assert.Null(BimiRecords.ParseBimi(["v=BIMI1; l=; a=;"])!.Location);
        Assert.Null(BimiRecords.ParseBimi(["v=DMARC1; p=reject"]));
    }

    [Theory]
    [InlineData("v=BIMI1; s=brand;", "brand")]
    [InlineData("v=BIMI1; s=Brand", "brand")]
    [InlineData("s=brand", null)]
    [InlineData("v=BIMI1; s=../x", null)]
    [InlineData(null, null)]
    public void SelectorHeader(string? header, string? selector)
    {
        Assert.Equal(selector, BimiRecords.ParseSelectorHeader(header));
    }

    [Theory]
    [InlineData("someone@Example.COM", "example.com")]
    [InlineData("someone@bücher.example", "xn--bcher-kva.example")]
    [InlineData("someone@localhost", null)]
    [InlineData("someone@192.168.1.1", null)]
    [InlineData("someone@[192.168.1.1]", null)]
    [InlineData("no-domain", null)]
    public void DomainOfAddress(string address, string? domain)
    {
        Assert.Equal(domain, BimiNames.DomainOf(address));
    }
}

public sealed class BimiSendersTests
{
    private static readonly BimiSenders Any = new(Options());

    private static SocklessMailOptions Options(params string[] trusted) =>
        new() { StorageDirectory = ".", BimiAuthServIds = trusted.ToHashSet(StringComparer.OrdinalIgnoreCase) };

    private static (InternetAddressList, HeaderList) Message(string from, params string[] headers)
    {
        var list = new HeaderList();
        foreach (var header in headers)
        {
            var colon = header.IndexOf(':');
            list.Add(header[..colon], header[(colon + 1)..].Trim());
        }

        return (InternetAddressList.Parse(from), list);
    }

    private static BimiDto? Find(BimiSenders senders, (InternetAddressList From, HeaderList Headers) message) =>
        senders.Find(message.From, message.Headers);

    [Fact]
    public void DmarcPassShowsTheFromDomain()
    {
        var sender = Find(Any, Message("Brand <news@brand.example>",
            "Authentication-Results: mx.local.test; dkim=pass header.d=brand.example; dmarc=pass header.from=brand.example"));
        Assert.Equal(new BimiDto("brand.example", null), sender);
    }

    [Fact]
    public void SelectorComesFromTheHeader()
    {
        var sender = Find(Any, Message("news@brand.example",
            "Authentication-Results: mx.local.test; dmarc=pass header.from=brand.example",
            "BIMI-Selector: v=BIMI1; s=spring;"));
        Assert.Equal(new BimiDto("brand.example", "spring"), sender);
    }

    [Theory]
    [InlineData("mx.local.test; dmarc=fail header.from=brand.example")]
    [InlineData("mx.local.test; dmarc=pass header.from=other.example")]
    [InlineData("mx.local.test; spf=fail smtp.mailfrom=brand.example")]
    [InlineData("this is not an authentication result")]
    public void NoLogoWithoutAMatchingDmarcPass(string results)
    {
        Assert.Null(Find(Any, Message("news@brand.example", $"Authentication-Results: {results}")));
    }

    [Fact]
    public void AlignedDkimCountsWhenTheServerGivesNoDmarcResult()
    {
        // As amavis at Loopia writes it.
        var sender = Find(Any, Message("MxToolBox Ping <abuse@mxtoolbox.com>",
            "Authentication-Results: s474.loopia.se (amavis); dkim=pass (1024-bit key)\r\n    header.d=mxtoolbox.com"));
        Assert.Equal(new BimiDto("mxtoolbox.com", null), sender);
        Assert.NotNull(Find(Any, Message("news@mail.brand.example", "Authentication-Results: mx.local.test; dkim=pass header.d=brand.example")));
        Assert.NotNull(Find(Any, Message("news@brand.example", "Authentication-Results: mx.local.test; spf=pass smtp.mailfrom=bounce@brand.example")));
    }

    [Theory]
    [InlineData("mx.local.test; dkim=pass header.d=mailer.example")]
    [InlineData("mx.local.test; dkim=pass header.d=evil.brand.example")]
    [InlineData("mx.local.test; dkim=fail header.d=brand.example")]
    [InlineData("mx.local.test; spf=pass smtp.mailfrom=bounce@mailer.example")]
    // An explicit DMARC verdict wins over an aligned signature.
    [InlineData("mx.local.test; dkim=pass header.d=brand.example; dmarc=fail header.from=brand.example")]
    public void UnalignedOrFailedAuthenticationShowsNoLogo(string results)
    {
        Assert.Null(Find(Any, Message("news@brand.example", $"Authentication-Results: {results}")));
    }

    [Theory]
    [InlineData("mx.local.test; dmarc=fail header.from=brand.example", true)]
    [InlineData("mx.local.test; dmarc=fail", true)]
    [InlineData("mx.local.test; dmarc=fail header.from=other.example", false)]
    [InlineData("mx.local.test; dmarc=pass header.from=brand.example", false)]
    [InlineData("mx.local.test; dmarc=none header.from=brand.example", false)]
    [InlineData("mx.local.test; spf=fail smtp.mailfrom=brand.example", false)]
    public void FailedDmarcReadsTheTrustedHeader(string results, bool failed)
    {
        var (from, headers) = Message("news@brand.example", $"Authentication-Results: {results}",
            "Authentication-Results: forged.example; dmarc=fail header.from=brand.example");
        Assert.Equal(failed, Any.FailedDmarc(from, headers));
    }

    [Fact]
    public void OnlyTheTopmostHeaderCounts()
    {
        // The sender's own (forged) verdict sits below the receiving server's.
        var message = Message("ceo@bank.example",
            "Authentication-Results: mx.local.test; dmarc=fail header.from=bank.example",
            "Authentication-Results: mx.local.test; dmarc=pass header.from=bank.example");
        Assert.Null(Find(Any, message));
    }

    [Fact]
    public void TrustedAuthServIdsSkipOtherHeaders()
    {
        var message = Message("news@brand.example",
            "Authentication-Results: forged.example; dmarc=pass header.from=brand.example",
            "Authentication-Results: mx.local.test; dmarc=pass header.from=brand.example");
        Assert.NotNull(Find(new BimiSenders(Options("mx.local.test")), message));
        Assert.Null(Find(new BimiSenders(Options("mx.other.test")), message));
    }

    [Fact]
    public void SeveralAuthorsShowNoLogo()
    {
        Assert.Null(Find(Any, Message("a@brand.example, b@brand.example",
            "Authentication-Results: mx.local.test; dmarc=pass header.from=brand.example")));
    }
}

public sealed class SvgLogoTests
{
    private const string Open = """<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" version="1.2" baseProfile="tiny-ps" viewBox="0 0 100 100">""";

    public const string Valid = Open + """
        <title>Brand</title>
        <defs><linearGradient id="g"><stop offset="0" stop-color="#f00"/></linearGradient></defs>
        <style>.a { fill: url(#g); }</style>
        <circle class="a" cx="50" cy="50" r="40" fill="url(#g)"/>
        <use xlink:href="#g"/>
        </svg>
        """;

    [Fact]
    public void AcceptsAStaticSvg()
    {
        Assert.True(SvgLogo.IsSafe(Encoding.UTF8.GetBytes(Valid)));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("""<circle r="1" onload="alert(1)"/>""")]
    [InlineData("""<image href="https://tracker.example/pixel.png"/>""")]
    [InlineData("""<use xlink:href="https://evil.example/sprite.svg#a"/>""")]
    [InlineData("""<foreignObject><div xmlns="http://www.w3.org/1999/xhtml">x</div></foreignObject>""")]
    [InlineData("""<circle r="1" style="fill: url(https://tracker.example/x)"/>""")]
    [InlineData("""<style>@import url("https://evil.example/x.css");</style>""")]
    [InlineData("""<set attributeName="href" to="javascript:alert(1)"/>""")]
    public void RefusesActiveOrExternalContent(string content)
    {
        Assert.False(SvgLogo.IsSafe(Encoding.UTF8.GetBytes(Open + content + "</svg>")));
    }

    [Fact]
    public void RefusesDtdsOtherRootsAndHugeFiles()
    {
        Assert.False(SvgLogo.IsSafe(Encoding.UTF8.GetBytes("""<!DOCTYPE svg [<!ENTITY x "y">]>""" + Open + "</svg>")));
        Assert.False(SvgLogo.IsSafe(Encoding.UTF8.GetBytes("""<html xmlns="http://www.w3.org/1999/xhtml"></html>""")));
        Assert.False(SvgLogo.IsSafe(Encoding.UTF8.GetBytes("<svg></svg>")));
        Assert.False(SvgLogo.IsSafe(Encoding.UTF8.GetBytes(Open + new string(' ', SvgLogo.MaxBytes) + "</svg>")));
        Assert.False(SvgLogo.IsSafe("not xml"u8.ToArray()));
    }
}

public sealed class BimiEndpointTests : IDisposable
{
    private readonly FakeDns _dns = new();
    private readonly FakeLogoServer _logos = new();
    private readonly TestApp _app;

    public BimiEndpointTests()
    {
        _app = new TestApp(services: services =>
        {
            services.AddSingleton<IBimiDns>(_dns);
            services.AddHttpClient(BimiResolver.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _logos);
        });
        _dns.Records["_dmarc.brand.example"] = ["v=DMARC1; p=reject"];
        _dns.Records["default._bimi.brand.example"] = ["v=BIMI1; l=https://cdn.brand.example/logo.svg"];
        _logos.Files["https://cdn.brand.example/logo.svg"] = SvgLogoTests.Valid;
    }

    public void Dispose() => _app.Dispose();

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ServesTheLogoAndAsksDnsOnce()
    {
        var client = _app.ClientAs("alice");
        for (var i = 0; i < 3; i++)
        {
            var response = await client.GetAsync("/api/bimi/brand.example", Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.True(response.Headers.CacheControl is { Private: true, MaxAge.TotalHours: 24 });
            Assert.Equal(SvgLogoTests.Valid, await response.Content.ReadAsStringAsync(Ct));
        }

        Assert.Equal(1, _dns.Queries.Count(q => q == "default._bimi.brand.example"));
        Assert.Single(_logos.Requests);
    }

    [Fact]
    public async Task ConcurrentRequestsShareOneLookup()
    {
        _logos.Delay = TimeSpan.FromMilliseconds(200);
        var client = _app.ClientAs("alice");
        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.GetAsync("/api/bimi/brand.example", Ct)));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Single(_logos.Requests);
    }

    [Fact]
    public async Task FallsBackToTheOrganizationalDomain()
    {
        _dns.Records["_dmarc.brand.example"] = ["v=DMARC1; p=quarantine; sp=reject"];
        var response = await _app.ClientAs("alice").GetAsync("/api/bimi/news.brand.example", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("default._bimi.news.brand.example", _dns.Queries);
    }

    [Fact]
    public async Task NoLogoWithoutEnforcedDmarc()
    {
        _dns.Records["_dmarc.brand.example"] = ["v=DMARC1; p=none"];
        var client = _app.ClientAs("alice");
        var response = await client.GetAsync("/api/bimi/brand.example", Ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(response.Headers.CacheControl is { Private: true, MaxAge.TotalHours: 1 });
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/bimi/brand.example", Ct)).StatusCode);
        Assert.Equal(1, _dns.Queries.Count(q => q == "_dmarc.brand.example"));
        Assert.Empty(_logos.Requests);
    }

    [Fact]
    public async Task NoLogoFromAPublicSuffixForItsCustomers()
    {
        // The registry of shop.example publishes a logo; customer.shop.example must not borrow it.
        _dns.Records["_dmarc.shop.example"] = ["v=DMARC1; p=reject; psd=y"];
        _dns.Records["default._bimi.shop.example"] = ["v=BIMI1; l=https://cdn.brand.example/logo.svg"];
        Assert.Equal(HttpStatusCode.NoContent, (await _app.ClientAs("alice").GetAsync("/api/bimi/x.customer.shop.example", Ct)).StatusCode);
        Assert.DoesNotContain("default._bimi.shop.example", _dns.Queries);
        Assert.Contains("default._bimi.customer.shop.example", _dns.Queries);
    }

    [Fact]
    public async Task RefusesUnsafeLogosAndPlainHttp()
    {
        _logos.Files["https://cdn.brand.example/logo.svg"] = """<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>""";
        Assert.Equal(HttpStatusCode.NoContent, (await _app.ClientAs("alice").GetAsync("/api/bimi/brand.example", Ct)).StatusCode);

        _dns.Records["_dmarc.other.example"] = ["v=DMARC1; p=reject"];
        _dns.Records["default._bimi.other.example"] = ["v=BIMI1; l=http://cdn.brand.example/logo.svg"];
        Assert.Equal(HttpStatusCode.NoContent, (await _app.ClientAs("alice").GetAsync("/api/bimi/other.example", Ct)).StatusCode);
    }

    [Fact]
    public async Task UsesTheSelector()
    {
        _dns.Records["spring._bimi.brand.example"] = ["v=BIMI1; l=https://cdn.brand.example/spring.svg"];
        _logos.Files["https://cdn.brand.example/spring.svg"] = SvgLogoTests.Valid.Replace("Brand", "Spring");
        var response = await _app.ClientAs("alice").GetAsync("/api/bimi/brand.example?s=spring", Ct);
        Assert.Contains("Spring", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task KeepsAKnownLogoWhenDnsFails()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        using var app = new TestApp(services: services =>
        {
            services.AddSingleton<IBimiDns>(_dns);
            services.AddSingleton<TimeProvider>(clock);
            services.AddHttpClient(BimiResolver.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => _logos);
        });
        var client = app.ClientAs("alice");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/bimi/brand.example", Ct)).StatusCode);

        clock.Now += BimiResolver.LogoLifetime + TimeSpan.FromMinutes(1);
        _dns.Failing = true;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/bimi/brand.example", Ct)).StatusCode);
    }

    [Theory]
    [InlineData("/api/bimi/localhost")]
    [InlineData("/api/bimi/10.0.0.1")]
    [InlineData("/api/bimi/brand.example?s=a.b")]
    public async Task RejectsNamesThatAreNotSenderDomains(string path)
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _app.ClientAs("alice").GetAsync(path, Ct)).StatusCode);
        Assert.Empty(_dns.Queries);
    }

    [Fact]
    public async Task NeedsAUser()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _app.CreateClient().GetAsync("/api/bimi/brand.example", Ct)).StatusCode);
    }

    private sealed class FakeDns : IBimiDns
    {
        public ConcurrentDictionary<string, string[]> Records { get; } = new();
        public ConcurrentQueue<string> Queries { get; } = new();
        public bool Failing { get; set; }

        public Task<IReadOnlyList<string>> TxtAsync(string name, CancellationToken ct)
        {
            Queries.Enqueue(name);
            return Failing
                ? Task.FromException<IReadOnlyList<string>>(new BimiDnsException("SERVFAIL"))
                : Task.FromResult<IReadOnlyList<string>>(Records.GetValueOrDefault(name) ?? []);
        }
    }

    private sealed class FakeLogoServer : HttpMessageHandler
    {
        public ConcurrentDictionary<string, string> Files { get; } = new();
        public ConcurrentQueue<Uri> Requests { get; } = new();
        public TimeSpan Delay { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Enqueue(request.RequestUri!);
            await Task.Delay(Delay, ct);
            return Files.TryGetValue(request.RequestUri!.ToString(), out var svg)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(svg, Encoding.UTF8, "image/svg+xml") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
