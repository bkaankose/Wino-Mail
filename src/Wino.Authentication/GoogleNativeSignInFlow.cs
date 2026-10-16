#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Authentication;

/// <summary>
/// The OpenID Connect pieces of a native Google sign-in for Wino Accounts through a system web session (iOS, Mac):
/// an authorization request with PKCE for a Google iOS client (custom-scheme redirect, no client secret), and the code
/// exchange that returns the ID token the API verifies. Access to Google APIs is not requested: the scopes are
/// <c>openid email profile</c> only.
/// </summary>
public static class GoogleNativeSignInFlow
{
    private const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string Scopes = "openid email profile";

    /// <summary>A fresh authorization request: state and PKCE verifier are random per attempt.</summary>
    public static GoogleNativeSignInRequest CreateRequest(string clientId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        var redirectUri = GoogleCustomSchemeRedirect.GetRedirectUri(clientId);
        var state = GoogleAuthorizationPkce.CreateState();
        var codeVerifier = GoogleAuthorizationPkce.CreateCodeVerifier();
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["response_type"] = "code",
            ["scope"] = Scopes,
            ["state"] = state
        };
        var authorizationUri = GoogleAuthorizationPkce.AppendChallenge(new Uri($"{AuthorizationEndpoint}?{Query(query)}"), codeVerifier);
        return new GoogleNativeSignInRequest(clientId, authorizationUri, redirectUri, state, codeVerifier);
    }

    /// <summary>
    /// Exchanges the authorization code from <paramref name="callbackUri"/> for the ID token. Throws
    /// <see cref="InvalidOperationException"/> when the callback carries an error, the state does not match, Google
    /// rejects the code, or the response has no ID token.
    /// </summary>
    public static async Task<string> ExchangeForIdTokenAsync(HttpClient httpClient, GoogleNativeSignInRequest request, Uri callbackUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(request);
        var callback = GoogleCustomSchemeRedirect.ParseCallback(callbackUri, request.RedirectUri, request.State);
        if (callback.Code is null)
            throw new InvalidOperationException(callback.Error == "invalid_state"
                ? "Google sign-in returned an invalid state value."
                : $"Google sign-in failed: {callback.Error}");

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = request.ClientId,
            ["code"] = callback.Code,
            ["code_verifier"] = request.CodeVerifier,
            ["redirect_uri"] = request.RedirectUri.AbsoluteUri,
            ["grant_type"] = "authorization_code"
        });
        using var response = await httpClient.PostAsync(TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google rejected the sign-in code ({(int)response.StatusCode}: {ReadError(body)}).");

        return ReadIdToken(body) ?? throw new InvalidOperationException("Google returned no ID token.");
    }

    /// <summary>The <c>id_token</c> of a token response, or null.</summary>
    public static string? ReadIdToken(string tokenResponse)
    {
        try
        {
            using var document = JsonDocument.Parse(tokenResponse);
            return document.RootElement.TryGetProperty("id_token", out var token) && token.ValueKind == JsonValueKind.String
                && token.GetString() is { Length: > 0 } value ? value : null;
        }
        catch (JsonException) { return null; }
    }

    // Only the OAuth error code is surfaced, never the full body.
    private static string ReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString() ?? "unknown" : "unknown";
        }
        catch (JsonException) { return "unknown"; }
    }

    private static string Query(IEnumerable<KeyValuePair<string, string>> values)
        => string.Join("&", System.Linq.Enumerable.Select(values, static pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
}

/// <summary>One authorization attempt: open <see cref="AuthorizationUri"/>, catch <see cref="RedirectUri"/>'s scheme.</summary>
public sealed record GoogleNativeSignInRequest(string ClientId, Uri AuthorizationUri, Uri RedirectUri, string State, string CodeVerifier);
