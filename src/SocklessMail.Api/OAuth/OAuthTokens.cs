using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;

namespace SocklessMail.Api.OAuth;

/// <summary>A token endpoint's answer. <see cref="RefreshToken"/> is null when the provider keeps the old one.</summary>
public sealed record OAuthTokenResponse(string AccessToken, string? RefreshToken, TimeSpan ExpiresIn, string? IdToken)
{
    public override string ToString() => $"OAuthTokenResponse {{ ExpiresIn = {ExpiresIn} }}";
}

/// <summary>Who signed in, from the ID token.</summary>
public sealed record OAuthIdentity(string Email, string? Name);

/// <summary>The provider refused the grant (revoked, expired, password changed): the user has to sign in again.</summary>
public sealed class OAuthGrantRevokedException(string error) : Exception($"The provider rejected the grant ({error}).");

/// <summary>The token endpoint could not be reached or answered something unusable.</summary>
public sealed class OAuthUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Calls a provider's token endpoint (RFC 6749 with PKCE, RFC 7636). Error bodies are never logged.</summary>
public sealed class OAuthTokens(IHttpClientFactory http, ILogger<OAuthTokens> logger)
{
    public const string HttpClientName = "oauth";

    public Task<OAuthTokenResponse> ExchangeCodeAsync(OAuthClientSettings client, string code, string codeVerifier, CancellationToken ct) =>
        RequestAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = client.RedirectUri,
            ["code_verifier"] = codeVerifier,
        }, ct);

    public Task<OAuthTokenResponse> RefreshAsync(OAuthClientSettings client, string refreshToken, CancellationToken ct) =>
        RequestAsync(client, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = client.Provider.Scope,
        }, ct);

    /// <summary>
    /// The address in the ID token. Its signature is not checked: the token came straight from the
    /// provider's token endpoint over TLS, in exchange for the client secret (OpenID Connect Core 3.1.3.7).
    /// Microsoft personal accounts may lack "email"; "preferred_username" is the sign-in address then.
    /// </summary>
    public static OAuthIdentity? ReadIdentity(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 })
        {
            return null;
        }

        try
        {
            using var payload = JsonDocument.Parse(WebEncoders.Base64UrlDecode(parts[1]));
            string? Claim(string name) =>
                payload.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

            var email = Claim("email") ?? Claim("preferred_username");
            return email is { Length: > 0 } && email.Contains('@') ? new OAuthIdentity(email.Trim(), Claim("name")) : null;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    private async Task<OAuthTokenResponse> RequestAsync(OAuthClientSettings client, Dictionary<string, string> form, CancellationToken ct)
    {
        // Client credentials in the body (client_secret_post): both Google and Microsoft accept it.
        form["client_id"] = client.ClientId;
        form["client_secret"] = client.ClientSecret;

        HttpResponseMessage response;
        try
        {
            response = await http.CreateClient(HttpClientName)
                .PostAsync(client.Provider.TokenEndpoint(client.Tenant), new FormUrlEncodedContent(form), ct);
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new OAuthUnavailableException($"{client.Provider.Name} could not be reached.", e);
        }

        using (response)
        {
            TokenBody? body = null;
            try
            {
                body = await response.Content.ReadFromJsonAsync<TokenBody>(ct);
            }
            catch (JsonException)
            {
            }

            if (response.IsSuccessStatusCode && body?.AccessToken is { Length: > 0 } accessToken)
            {
                return new OAuthTokenResponse(accessToken, body.RefreshToken, TimeSpan.FromSeconds(Math.Clamp(body.ExpiresIn ?? 3600, 60, 86400)), body.IdToken);
            }

            // invalid_grant: the refresh token or code is no longer valid. Anything else is our or the provider's fault.
            if (body?.Error == "invalid_grant")
            {
                throw new OAuthGrantRevokedException(body.Error);
            }

            logger.LogWarning("{Provider} token endpoint answered {StatusCode} ({Error})", client.Provider.Id, (int)response.StatusCode, body?.Error ?? "no error code");
            throw new OAuthUnavailableException(body?.Error switch
            {
                "invalid_client" or "unauthorized_client" => $"{client.Provider.Name} rejected the app's client id or secret. Ask your administrator to check the OAuth settings.",
                _ => $"{client.Provider.Name} refused to issue a token ({body?.Error ?? $"HTTP {(int)response.StatusCode}"}).",
            });
        }
    }

    private sealed record TokenBody(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] int? ExpiresIn,
        [property: JsonPropertyName("id_token")] string? IdToken,
        [property: JsonPropertyName("error")] string? Error);

    /// <summary>The URL the user's browser goes to, with PKCE and the flow's state.</summary>
    public static string AuthorizeUrl(OAuthClientSettings client, OAuthFlow flow)
    {
        var query = new Dictionary<string, string?>(client.Provider.AuthorizeParameters.ToDictionary(p => p.Key, p => (string?)p.Value))
        {
            ["response_type"] = "code",
            ["client_id"] = client.ClientId,
            ["redirect_uri"] = flow.RedirectUri,
            ["scope"] = client.Provider.Scope,
            ["state"] = flow.State,
            ["code_challenge"] = OAuthFlows.Challenge(flow),
            ["code_challenge_method"] = "S256",
        };
        return QueryHelpers.AddQueryString(client.Provider.AuthorizeEndpoint(client.Tenant).ToString(), query);
    }
}
