using System.Text;
using SocklessMail.Api.AppApi;

namespace SocklessMail.Tests;

public class AppApiHeadersTests
{
    private static readonly AppApiOptions Options = TestApp.Options;

    private static string Auth(string raw) => Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));

    [Fact]
    public void Valid_headers_yield_the_user()
    {
        Assert.True(AppApiHeaders.TryValidate("sockless_mail", "0.1.0", Auth($"alice:{Options.AppSecret}"), Options, out var user));
        Assert.Equal("alice", user);
    }

    [Fact]
    public void Empty_user_is_valid_system_context()
    {
        Assert.True(AppApiHeaders.TryValidate("sockless_mail", "0.1.0", Auth($":{Options.AppSecret}"), Options, out var user));
        Assert.Equal("", user);
    }

    [Fact]
    public void Build_and_validate_round_trip()
    {
        var header = AppApiHeaders.BuildAuthorization("bob", Options.AppSecret);
        Assert.True(AppApiHeaders.TryValidate("sockless_mail", "0.1.0", header, Options, out var user));
        Assert.Equal("bob", user);
    }

    [Theory]
    [InlineData("sockless_mail", "0.1.0", "alice:wrong-secret")]
    [InlineData("sockless_mail", "0.1.0", "alice:")]
    [InlineData("sockless_mail", "0.1.0", "alice")]
    [InlineData("other_app", "0.1.0", "alice:" + TestApp.Secret)]
    [InlineData("SOCKLESS_MAIL", "0.1.0", "alice:" + TestApp.Secret)]
    [InlineData("sockless_mail", "", "alice:" + TestApp.Secret)]
    [InlineData("", "0.1.0", "alice:" + TestApp.Secret)]
    public void Invalid_headers_are_rejected(string appId, string version, string rawAuth)
    {
        Assert.False(AppApiHeaders.TryValidate(appId, version, Auth(rawAuth), Options, out var user));
        Assert.Null(user);
    }

    [Fact]
    public void Secret_with_a_suffix_is_rejected()
    {
        // The user id ends at the first colon, so "alice:secret:x" carries the secret "secret:x".
        Assert.False(AppApiHeaders.TryValidate("sockless_mail", "0.1.0", Auth($"alice:{Options.AppSecret}:x"), Options, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("//79")] // valid base64 of invalid UTF-8
    public void Malformed_authorization_is_rejected(string header)
    {
        Assert.False(AppApiHeaders.TryValidate("sockless_mail", "0.1.0", header, Options, out _));
    }
}
