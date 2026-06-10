using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Authentication;

public sealed class OidcTokenClient : IOidcTokenClient
{
    private static readonly HttpClient HttpClient = new();

    public async Task<OidcDiscoveryDocument> GetDiscoveryDocumentAsync(string authority, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authority))
            throw new ArgumentException("Authority is required.", nameof(authority));

        // Credentials and tokens travel to these endpoints, so reject downgradeable URLs.
        EnsureHttps(authority, "authority");

        var url = authority.TrimEnd('/') + "/.well-known/openid-configuration";

        using var response = await HttpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new OidcTokenException($"Discovery request to {url} failed with {(int)response.StatusCode}.");

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var authorizationEndpoint = GetString(root, "authorization_endpoint")
            ?? throw new OidcTokenException("Discovery document is missing authorization_endpoint.");
        var tokenEndpoint = GetString(root, "token_endpoint")
            ?? throw new OidcTokenException("Discovery document is missing token_endpoint.");

        EnsureHttps(authorizationEndpoint, "authorization_endpoint");
        EnsureHttps(tokenEndpoint, "token_endpoint");

        return new OidcDiscoveryDocument
        {
            Issuer = GetString(root, "issuer"),
            AuthorizationEndpoint = authorizationEndpoint,
            TokenEndpoint = tokenEndpoint,
        };
    }

    private static void EnsureHttps(string url, string name)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new OidcTokenException($"The OIDC {name} must be an absolute HTTPS URL.");
    }

    public PkcePair CreatePkcePair()
    {
        var verifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new PkcePair(verifier, challenge);
    }

    public string BuildAuthorizationUrl(OidcDiscoveryDocument discovery, OidcConfiguration configuration, string codeChallenge, string state)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = configuration.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = configuration.RedirectUri,
            ["resource"] = configuration.Resource,
            ["scope"] = configuration.Scope,
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
        };

        var query = string.Join("&", parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value ?? string.Empty)}"));
        return discovery.AuthorizationEndpoint + "?" + query;
    }

    public Task<OidcTokenSet> ExchangeAuthorizationCodeAsync(OidcDiscoveryDocument discovery, OidcConfiguration configuration, string code, string codeVerifier, CancellationToken cancellationToken = default)
        => PostTokenRequestAsync(discovery, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = configuration.RedirectUri,
            ["client_id"] = configuration.ClientId,
            ["code_verifier"] = codeVerifier,
            ["resource"] = configuration.Resource,
        }, cancellationToken);

    public Task<OidcTokenSet> RefreshAsync(OidcDiscoveryDocument discovery, OidcConfiguration configuration, string refreshToken, CancellationToken cancellationToken = default)
        => PostTokenRequestAsync(discovery, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = configuration.ClientId,
            ["resource"] = configuration.Resource,
            ["scope"] = configuration.Scope,
        }, cancellationToken);

    private static async Task<OidcTokenSet> PostTokenRequestAsync(OidcDiscoveryDocument discovery, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await HttpClient.PostAsync(discovery.TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw new OidcTokenException($"Token endpoint returned {(int)response.StatusCode}: {Truncate(body)}");

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var accessToken = GetString(root, "access_token")
            ?? throw new OidcTokenException("Token response did not contain an access_token.");

        var expiresIn = root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var seconds)
            ? seconds
            : 3600;

        return new OidcTokenSet
        {
            AccessToken = accessToken,
            RefreshToken = GetString(root, "refresh_token"),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(expiresIn),
        };
    }

    private static string GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Truncate(string value)
        => string.IsNullOrEmpty(value) || value.Length <= 256 ? value : value[..256] + "...";
}
