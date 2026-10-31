using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static SocklessMail.Tests.AccountTestData;

namespace SocklessMail.Tests;

public sealed class SendLaterPresetTests : IDisposable
{
    private const string Path = "/api/send-later/presets";

    private readonly TestApp _app = new();
    private readonly HttpClient _alice;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public SendLaterPresetTests() => _alice = _app.ClientAs("alice");

    public void Dispose() => _app.Dispose();

    private async Task<JsonElement> PresetsAsync(HttpClient client) =>
        (await JsonAsync(await client.GetAsync(Path, Ct))).GetProperty("presets");

    private Task<HttpResponseMessage> PutAsync(params object[] presets) => _alice.PutAsJsonAsync(Path, new { presets }, Ct);

    [Fact]
    public async Task Without_own_presets_the_built_in_ones_apply()
    {
        Assert.Equal(JsonValueKind.Null, (await PresetsAsync(_alice)).ValueKind);
    }

    [Fact]
    public async Task Own_presets_are_kept_in_order_cleaned_and_reset()
    {
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(
            new { id = "early", label = "  Early bird ", kind = "tomorrow", time = "06:30", weekday = 3, hours = 5 },
            new { id = "friday", label = "Friday afternoon", kind = "weekday", time = "15:00", weekday = 5, showOn = new[] { 4, 1, 1 } },
            new { id = "soon", label = "In two hours", kind = "hours", hours = 2, time = "09:00", showOn = new[] { 0, 1, 2, 3, 4, 5, 6 } })).StatusCode);

        var presets = (await PresetsAsync(_alice)).EnumerateArray().ToArray();
        Assert.Equal(["early", "friday", "soon"], presets.Select(p => p.GetProperty("id").GetString()));
        Assert.Equal("Early bird", presets[0].GetProperty("label").GetString());
        // Fields that do not belong to the kind are dropped.
        Assert.Equal(JsonValueKind.Null, presets[0].GetProperty("weekday").ValueKind);
        Assert.Equal(JsonValueKind.Null, presets[0].GetProperty("hours").ValueKind);
        Assert.Equal([1, 4], presets[1].GetProperty("showOn").EnumerateArray().Select(d => d.GetInt32()));
        Assert.Equal(JsonValueKind.Null, presets[2].GetProperty("time").ValueKind);
        // Every day is no limit.
        Assert.Equal(JsonValueKind.Null, presets[2].GetProperty("showOn").ValueKind);

        // Others do not see them.
        Assert.Equal(JsonValueKind.Null, (await PresetsAsync(_app.ClientAs("bob"))).ValueKind);

        Assert.Equal(HttpStatusCode.NoContent, (await _alice.DeleteAsync(Path, Ct)).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await PresetsAsync(_alice)).ValueKind);
    }

    [Fact]
    public async Task An_empty_list_is_kept_as_no_presets()
    {
        Assert.Equal(HttpStatusCode.OK, (await PutAsync()).StatusCode);
        Assert.Empty((await PresetsAsync(_alice)).EnumerateArray());
    }

    [Theory]
    [InlineData("id", "{\"id\":\"Bad Id\",\"label\":\"x\",\"kind\":\"today\",\"time\":\"08:00\"}")]
    [InlineData("label", "{\"id\":\"a\",\"label\":\"  \",\"kind\":\"today\",\"time\":\"08:00\"}")]
    [InlineData("kind", "{\"id\":\"a\",\"label\":\"x\",\"kind\":\"yesterday\",\"time\":\"08:00\"}")]
    [InlineData("time", "{\"id\":\"a\",\"label\":\"x\",\"kind\":\"today\",\"time\":\"24:00\"}")]
    [InlineData("time", "{\"id\":\"a\",\"label\":\"x\",\"kind\":\"tomorrow\"}")]
    [InlineData("weekday", "{\"id\":\"a\",\"label\":\"x\",\"kind\":\"weekday\",\"time\":\"08:00\",\"weekday\":7}")]
    [InlineData("hours", "{\"id\":\"a\",\"label\":\"x\",\"kind\":\"hours\",\"hours\":0}")]
    [InlineData("showOn", "{\"id\":\"a\",\"label\":\"x\",\"kind\":\"today\",\"time\":\"08:00\",\"showOn\":[9]}")]
    public async Task Invalid_presets_are_refused(string field, string preset)
    {
        var response = await _alice.PutAsync(Path, new StringContent($"{{\"presets\":[{preset}]}}", System.Text.Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty($"presets[0].{field}", out _), errors.ToString());
        Assert.Equal(JsonValueKind.Null, (await PresetsAsync(_alice)).ValueKind);
    }

    [Fact]
    public async Task Ids_must_be_unique_and_the_list_short()
    {
        var twice = new { id = "a", label = "x", kind = "today", time = "08:00" };
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(twice, twice)).StatusCode);
        var many = Enumerable.Range(0, 21).Select(i => (object)new { id = $"p{i}", label = "x", kind = "hours", hours = 1 }).ToArray();
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(many)).StatusCode);
    }
}
