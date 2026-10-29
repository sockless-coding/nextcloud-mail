using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using SocklessMail.Api.Contacts;

namespace SocklessMail.Tests;

/// <summary>Recipient suggestions from Nextcloud address books and adding senders, against a fake CardDAV server.</summary>
public sealed class ContactsTests : IDisposable
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));
    private readonly TestApp _app;
    private readonly FakeCardDav _carddav = new();
    private readonly HttpClient _alice;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public ContactsTests()
    {
        _app = new TestApp(services: s => s.AddSingleton<TimeProvider>(_clock));
        _app.Nextcloud.Respond = _carddav.Respond;
        _alice = _app.ClientAs("alice");

        _carddav.AddBook("alice", "contacts", "Contacts");
        _carddav.AddBook("alice", "z-server-generated--system", "Accounts", writable: false);
        _carddav.AddBook("alice", "z-app-generated--contactsinteraction--recent", "Recently contacted", writable: false);
        _carddav.AddCard("alice", "contacts", "petra.vcf", """
            BEGIN:VCARD
            VERSION:3.0
            UID:petra
            FN:Petra Probe
            EMAIL;TYPE=WORK:petra@work.example
            EMAIL;TYPE=HOME:petra@home.example
            CATEGORIES:Family,Climbing
            ORG:Probe Inc.
            END:VCARD
            """);
        _carddav.AddCard("alice", "contacts", "olaf.vcf", """
            BEGIN:VCARD
            VERSION:3.0
            UID:olaf
            N:Petrov;Olaf;;;
            item1.EMAIL;TYPE=INTERNET:olaf@example.org
            CATEGORIES:Climbing
            END:VCARD
            """);
        _carddav.AddCard("alice", "contacts", "nomail.vcf", "BEGIN:VCARD\nVERSION:3.0\nFN:Petra Phoneonly\nTEL:123\nEND:VCARD");
        _carddav.AddCard("alice", "z-server-generated--system", "bob.vcf", "BEGIN:VCARD\nVERSION:3.0\nFN:Bob Builder\nEMAIL:bob@cloud.example\nEND:VCARD");
        // Also in her own book: listed once.
        _carddav.AddCard("alice", "z-app-generated--contactsinteraction--recent", "p.vcf", "BEGIN:VCARD\nVERSION:3.0\nFN:petra@work.example\nEMAIL:petra@work.example\nCATEGORIES:Recent\nEND:VCARD");
    }

    public void Dispose() => _app.Dispose();

    private async Task<JsonElement> GetAsync(string path)
    {
        var response = await _alice.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
    }

    private static string[] Addresses(JsonElement result) =>
        [.. result.GetProperty("contacts").EnumerateArray().Select(c => c.GetProperty("address").GetString()!)];

    [Fact]
    public async Task Search_finds_names_addresses_and_organizations_word_starts_first()
    {
        var result = await GetAsync("/api/contacts/search?q=petr");

        // "Petra" and "Petrov" start a word, so both rank first, by name; every address once,
        // from the user's own book (not "Recently contacted").
        Assert.Equal(["olaf@example.org", "petra@work.example", "petra@home.example"], Addresses(result));
        Assert.Equal("Olaf Petrov", result.GetProperty("contacts")[0].GetProperty("name").GetString());
        var petra = result.GetProperty("contacts")[1];
        Assert.Equal("Petra Probe", petra.GetProperty("name").GetString());
        Assert.Equal("contacts/petra.vcf", petra.GetProperty("id").GetString());
        Assert.Equal("Contacts", petra.GetProperty("addressBook").GetString());

        Assert.Equal(["bob@cloud.example"], Addresses(await GetAsync("/api/contacts/search?q=BUILD")));
        Assert.Equal(["petra@work.example", "petra@home.example"], Addresses(await GetAsync("/api/contacts/search?q=probe inc")));
        Assert.Empty(Addresses(await GetAsync("/api/contacts/search?q=")));
    }

    [Fact]
    public async Task Groups_list_each_members_first_address()
    {
        var result = await GetAsync("/api/contacts/search?q=climb");

        var group = Assert.Single(result.GetProperty("groups").EnumerateArray());
        Assert.Equal("Climbing", group.GetProperty("name").GetString());
        Assert.Equal(["olaf@example.org", "petra@work.example"],
            group.GetProperty("members").EnumerateArray().Select(m => m.GetProperty("address").GetString()));
        // Collected addresses are never a group.
        Assert.Empty((await GetAsync("/api/contacts/search?q=recent")).GetProperty("groups").EnumerateArray());
    }

    [Fact]
    public async Task Books_are_fetched_again_only_when_they_changed()
    {
        await GetAsync("/api/contacts/search?q=a");
        await GetAsync("/api/contacts/search?q=b");
        Assert.Equal(1, _carddav.Reports["contacts"]);
        Assert.Single(_app.Nextcloud.Requests, r => r.Method.Method == "PROPFIND");

        _clock.Advance(ContactDirectory.Fresh);
        _carddav.AddCard("alice", "contacts", "new.vcf", "BEGIN:VCARD\nVERSION:3.0\nFN:Nina New\nEMAIL:nina@example.org\nEND:VCARD");

        Assert.Equal(["nina@example.org"], Addresses(await GetAsync("/api/contacts/search?q=nina")));
        Assert.Equal(2, _carddav.Reports["contacts"]);
        Assert.Equal(1, _carddav.Reports["z-server-generated--system"]);
    }

    [Fact]
    public async Task Users_only_see_their_own_address_books()
    {
        var bob = _app.ClientAs("bob");
        var body = JsonDocument.Parse(await bob.GetStringAsync("/api/contacts/search?q=petra", Ct)).RootElement;
        Assert.Empty(Addresses(body));
    }

    [Fact]
    public async Task Address_books_say_which_are_writable()
    {
        var books = (await GetAsync("/api/contacts/address-books")).EnumerateArray().ToArray();

        Assert.Equal("contacts", books[0].GetProperty("id").GetString());
        Assert.True(books[0].GetProperty("writable").GetBoolean());
        Assert.All(books[1..], b => Assert.False(b.GetProperty("writable").GetBoolean()));
    }

    [Fact]
    public async Task A_sender_is_added_as_a_new_contact()
    {
        var response = await _alice.PostAsJsonAsync("/api/contacts",
            new { addressBookId = "contacts", name = "Carl Customer", address = "carl@shop.example" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var card = Assert.Single(_carddav.Cards("alice", "contacts"), c => c.EndsWith(".vcf") && !c.StartsWith("petra") && !c.StartsWith("olaf") && !c.StartsWith("nomail"));
        var vcard = VCard.Parse(_carddav.Card("alice", "contacts", card)!);
        Assert.Equal("Carl Customer", vcard.Name);
        Assert.Equal(["carl@shop.example"], vcard.Emails);
        // Found right away: the directory does not wait for its next check.
        Assert.Equal(["carl@shop.example"], Addresses(await GetAsync("/api/contacts/search?q=carl")));
        Assert.Equal("Carl Customer", Assert.Single((await GetAsync("/api/contacts/by-address?address=CARL@shop.example")).EnumerateArray()).GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("z-server-generated--system", "carl@shop.example", "addressBookId")]
    [InlineData("missing", "carl@shop.example", "addressBookId")]
    [InlineData("contacts", "not an address", "address")]
    public async Task Creating_checks_the_book_and_the_address(string book, string address, string field)
    {
        var response = await _alice.PostAsJsonAsync("/api/contacts", new { addressBookId = book, name = "C", address }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await response.Content.ReadAsStringAsync(Ct));
        Assert.DoesNotContain(_app.Nextcloud.Requests, r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task An_address_is_added_to_an_existing_contact_keeping_the_rest()
    {
        var response = await _alice.PostAsJsonAsync("/api/contacts/addresses", new { contactId = "contacts/petra.vcf", address = "petra@new.example" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = _carddav.Card("alice", "contacts", "petra.vcf")!;
        Assert.Equal(["petra@work.example", "petra@home.example", "petra@new.example"], VCard.Parse(text).Emails);
        Assert.Contains("CATEGORIES:Family,Climbing\r\nORG:Probe Inc.\r\n", text);
        Assert.EndsWith("EMAIL;TYPE=INTERNET:petra@new.example\r\nEND:VCARD", text.TrimEnd());
        Assert.Contains(_app.Nextcloud.Requests, r => r.Method == HttpMethod.Put && r.Headers["If-Match"] == "\"v1\"");
    }

    [Fact]
    public async Task A_contact_changed_meanwhile_is_not_overwritten()
    {
        var carddav = _carddav;
        _app.Nextcloud.Respond = r =>
        {
            // Another client saves the card between our GET and our PUT.
            if (r.Method == HttpMethod.Put)
            {
                carddav.Touch("alice", "contacts", "petra.vcf");
            }

            return carddav.Respond(r);
        };

        var response = await _alice.PostAsJsonAsync("/api/contacts/addresses", new { contactId = "contacts/petra.vcf", address = "petra@new.example" }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain("petra@new.example", _carddav.Card("alice", "contacts", "petra.vcf"));
    }

    [Theory]
    [InlineData("contacts/../../files/alice/x")]
    [InlineData("contacts")]
    [InlineData("../petra.vcf")]
    public async Task Contact_ids_cannot_leave_the_address_book_home(string contactId)
    {
        var response = await _alice.PostAsJsonAsync("/api/contacts/addresses", new { contactId, address = "a@b.example" }, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_app.Nextcloud.Requests);
    }

    [Fact]
    public void VCard_reads_folded_lines_escapes_and_names()
    {
        var card = VCard.Parse("BEGIN:VCARD\r\nVERSION:4.0\r\nFN:Anna \\, the \r\n Second\r\nEMAIL;PREF=1:mailto:anna@example.org\r\nCATEGORIES:a\\,b,c\r\nX-NOTE;X-P=\"x:y\":ignored\r\nEND:VCARD\r\n");

        Assert.Equal("Anna , the Second", card.Name);
        Assert.Equal(["anna@example.org"], card.Emails);
        Assert.Equal(["a,b", "c"], card.Groups);
        Assert.Equal("Dr. Jane Q. Public", VCard.Parse("BEGIN:VCARD\nN:Public;Jane;Q.;Dr.;\nEMAIL:j@x.example\nEND:VCARD").Name);
    }

    [Fact]
    public void A_new_vcard_escapes_and_folds()
    {
        var text = VCard.Create("u-1", "Smith; Jones, " + new string('x', 80), "s@example.org");

        Assert.All(text.Split("\r\n"), line => Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 75));
        var card = VCard.Parse(text);
        Assert.Equal("Smith; Jones, " + new string('x', 80), card.Name);
        Assert.Equal("u-1", card.Uid);
        Assert.Equal(["s@example.org"], card.Emails);
    }
}
