#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Authorization;

namespace Wino.Authentication.OAuth;

/// <summary>The endpoints a sign-in or refresh talks to, after HTTPS and host checks.</summary>
public sealed record OAuthEndpoints(
    string Issuer,
    Uri AuthorizationEndpoint,
    Uri TokenEndpoint,
    Uri? RevocationEndpoint,
    Uri? RegistrationEndpoint,
    OAuthServerMetadata? Metadata);

public sealed record OAuthTokenResult(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAtUtc, string? Scope);

/// <summary>
/// A failed OAuth protocol exchange. <see cref="OAuthError"/> is the RFC 6749 error code when the server sent one.
/// <see cref="IsTransient"/> means the issuer was unavailable or answered without an OAuth error (5xx, timeout,
/// no network, a proxy page); the grant itself may still be fine.
/// </summary>
public sealed record OAuthClientRegistration(string ClientId, OAuthRedirectKind RedirectKind);

public sealed class OAuthProtocolException : Exception
{
    public OAuthProtocolException(string message, string? oauthError, int? statusCode, bool isTransient, Exception? innerException = null)
        : base(message, innerException)
    {
        OAuthError = oauthError;
        StatusCode = statusCode;
        IsTransient = isTransient;
    }

    public string? OAuthError { get; }
    public int? StatusCode { get; }
    public bool IsTransient { get; }
}

/// <summary>Stateless OAuth 2.0 protocol work: metadata, dynamic registration, authorization URL, token exchange, refresh and revocation.</summary>
public interface IOAuthProtocolClient
{
    /// <summary>Metadata for <paramref name="issuer"/>: RFC 8414 first, then OIDC discovery. The document's issuer must match.</summary>
    Task<OAuthServerMetadata> GetMetadataAsync(string issuer, CancellationToken cancellationToken = default);

    /// <summary>
    /// The metadata document published on <c>https://{host}/.well-known/…</c>, without the issuer check, or null when
    /// none is published. Discovery uses it as a hint and then loads the named issuer's own metadata.
    /// </summary>
    Task<OAuthServerMetadata?> FindHostMetadataAsync(string host, CancellationToken cancellationToken = default);

    Task<OAuthEndpoints> ResolveEndpointsAsync(AccountOAuthProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// The endpoints in <paramref name="metadata"/>, checked like <see cref="ResolveEndpointsAsync"/> checks them. The
    /// metadata must already belong to <paramref name="issuer"/>.
    /// </summary>
    OAuthEndpoints CreateEndpoints(string issuer, OAuthServerMetadata metadata, OAuthTrustSource trustSource);

    /// <summary>
    /// RFC 7591 registration as a public native client. Registers the custom-scheme and loopback redirects when
    /// <paramref name="preferCustomScheme"/> is set and falls back to loopback only when the server refuses the scheme.
    /// Without <paramref name="allowLoopback"/> a refused scheme fails the registration instead.
    /// </summary>
    Task<OAuthClientRegistration> RegisterClientAsync(Uri registrationEndpoint, string? scopes, bool preferCustomScheme, CancellationToken cancellationToken = default, bool allowLoopback = true);

    Uri BuildAuthorizationUri(
        OAuthEndpoints endpoints,
        string clientId,
        string? scopes,
        string? resource,
        Uri redirectUri,
        string state,
        string codeChallenge,
        string? loginHint);

    Task<OAuthTokenResult> ExchangeCodeAsync(
        Uri tokenEndpoint,
        string clientId,
        string code,
        string codeVerifier,
        Uri redirectUri,
        string? resource,
        CancellationToken cancellationToken = default);

    Task<OAuthTokenResult> RefreshAsync(
        Uri tokenEndpoint,
        string clientId,
        string refreshToken,
        string? resource,
        CancellationToken cancellationToken = default);

    /// <summary>RFC 7009 revocation.</summary>
    Task RevokeAsync(Uri revocationEndpoint, string clientId, string token, string tokenTypeHint, CancellationToken cancellationToken = default);
}

public sealed class OAuthProtocolClient : IOAuthProtocolClient
{
    public const string ClientName = "Wino Mail";

    /// <summary>The loopback redirect registered with servers. RFC 8252 servers accept any port on it.</summary>
    public static readonly Uri LoopbackRedirectUri = new("http://127.0.0.1/");

    private const string OAuthWellKnownPath = "/.well-known/oauth-authorization-server";
    private const string OpenIdWellKnownPath = "/.well-known/openid-configuration";
    private const int DefaultLifetimeSeconds = 3600;
    private const int MinimumLifetimeSeconds = 60;
    private const int MaximumLifetimeSeconds = 366 * 24 * 3600;

    private static readonly ILogger Logger = Log.ForContext<OAuthProtocolClient>();

    // Redirects are not followed: a token POST or metadata fetch must never be steered to another URL.
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 1024 * 1024
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinoMail", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public async Task<OAuthServerMetadata> GetMetadataAsync(string issuer, CancellationToken cancellationToken = default)
    {
        var issuerUri = RequireHttps(issuer, "issuer");
        OAuthProtocolException? lastFailure = null;

        foreach (var url in GetMetadataUrls(issuerUri))
        {
            try
            {
                var metadata = await FetchMetadataDocumentAsync(url, cancellationToken).ConfigureAwait(false);
                if (metadata is null)
                    continue;

                if (!IssuerEquals(metadata.Issuer, issuer))
                {
                    lastFailure = new OAuthProtocolException("The metadata document names a different issuer.", "issuer_mismatch", null, isTransient: false);
                    continue;
                }

                return metadata;
            }
            catch (OAuthProtocolException ex)
            {
                lastFailure = ex;
            }
        }

        throw lastFailure ?? new OAuthProtocolException("The issuer publishes no OAuth metadata.", "metadata_not_found", null, isTransient: false);
    }

    public async Task<OAuthServerMetadata?> FindHostMetadataAsync(string host, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host) || Uri.CheckHostName(host.Trim()) == UriHostNameType.Unknown)
            return null;

        var hostUri = new Uri($"https://{host.Trim()}/");
        foreach (var url in GetMetadataUrls(hostUri))
        {
            var metadata = await FetchMetadataDocumentAsync(url, cancellationToken).ConfigureAwait(false);
            if (metadata is not null && !string.IsNullOrWhiteSpace(metadata.Issuer))
                return metadata;
        }

        return null;
    }

    public async Task<OAuthEndpoints> ResolveEndpointsAsync(AccountOAuthProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        RequireHttps(profile.Issuer, "issuer");

        var hasExplicitEndpoints = !string.IsNullOrWhiteSpace(profile.AuthorizationEndpoint) && !string.IsNullOrWhiteSpace(profile.TokenEndpoint);
        var metadata = hasExplicitEndpoints
            ? null
            : await GetMetadataAsync(profile.Issuer, cancellationToken).ConfigureAwait(false);

        var endpoints = BuildEndpoints(
            profile.Issuer,
            Prefer(profile.AuthorizationEndpoint, metadata?.AuthorizationEndpoint),
            Prefer(profile.TokenEndpoint, metadata?.TokenEndpoint),
            Prefer(profile.RevocationEndpoint, metadata?.RevocationEndpoint),
            metadata?.RegistrationEndpoint,
            metadata,
            profile.TrustSource);

        return endpoints;
    }

    public OAuthEndpoints CreateEndpoints(string issuer, OAuthServerMetadata metadata, OAuthTrustSource trustSource)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return BuildEndpoints(issuer, metadata.AuthorizationEndpoint, metadata.TokenEndpoint, metadata.RevocationEndpoint,
            metadata.RegistrationEndpoint, metadata, trustSource);
    }

    public async Task<OAuthClientRegistration> RegisterClientAsync(Uri registrationEndpoint, string? scopes, bool preferCustomScheme, CancellationToken cancellationToken = default, bool allowLoopback = true)
    {
        if (preferCustomScheme || !allowLoopback)
        {
            try
            {
                var clientId = await RegisterAsync(registrationEndpoint, scopes,
                    [OAuthRedirect.CustomSchemeRedirectUri.AbsoluteUri, LoopbackRedirectUri.AbsoluteUri], cancellationToken).ConfigureAwait(false);
                return new OAuthClientRegistration(clientId, OAuthRedirectKind.CustomScheme);
            }
            catch (OAuthProtocolException ex) when (ex.OAuthError == "invalid_redirect_uri" && allowLoopback)
            {
                // Some servers (Stalwart) accept only reverse-domain private-use schemes, so wino:// is refused.
            }
        }

        var loopbackClientId = await RegisterAsync(registrationEndpoint, scopes, [LoopbackRedirectUri.AbsoluteUri], cancellationToken).ConfigureAwait(false);
        return new OAuthClientRegistration(loopbackClientId, OAuthRedirectKind.Loopback);
    }

    private async Task<string> RegisterAsync(Uri registrationEndpoint, string? scopes, List<string> redirectUris, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registrationEndpoint);
        RequireHttps(registrationEndpoint.AbsoluteUri, "registration_endpoint");

        var request = new OAuthClientRegistrationRequest
        {
            ClientName = ClientName,
            RedirectUris = redirectUris,
            GrantTypes = ["authorization_code", "refresh_token"],
            ResponseTypes = ["code"],
            TokenEndpointAuthMethod = "none",
            ApplicationType = "native",
            Scope = string.IsNullOrWhiteSpace(scopes) ? null : scopes.Trim()
        };

        var body = JsonSerializer.Serialize(request, OAuthJsonContext.Default.OAuthClientRegistrationRequest);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await SendAsync(() => HttpClient.PostAsync(registrationEndpoint, content, cancellationToken), "registration", cancellationToken).ConfigureAwait(false);
        var responseBody = await ReadBodyAsync(response, "registration", cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw CreateErrorResponseException("Dynamic client registration failed.", response.StatusCode, responseBody, "registration");

        OAuthClientRegistrationResponse? registration;
        try
        {
            registration = JsonSerializer.Deserialize(responseBody, OAuthJsonContext.Default.OAuthClientRegistrationResponse);
        }
        catch (JsonException ex)
        {
            throw new OAuthProtocolException("The registration response is not valid JSON.", "invalid_registration_response", (int)response.StatusCode, isTransient: true, ex);
        }

        if (string.IsNullOrWhiteSpace(registration?.ClientId))
            throw new OAuthProtocolException("The registration response has no client_id.", "invalid_registration_response", (int)response.StatusCode, isTransient: false);

        return registration.ClientId;
    }

    public Uri BuildAuthorizationUri(
        OAuthEndpoints endpoints,
        string clientId,
        string? scopes,
        string? resource,
        Uri redirectUri,
        string state,
        string codeChallenge,
        string? loginHint)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(redirectUri);

        var parameters = new List<KeyValuePair<string, string>>();
        Add(parameters, "response_type", "code");
        Add(parameters, "client_id", clientId);
        Add(parameters, "redirect_uri", redirectUri.AbsoluteUri);
        Add(parameters, "scope", scopes);
        Add(parameters, "resource", resource);
        Add(parameters, "state", state);
        Add(parameters, "code_challenge", codeChallenge);
        Add(parameters, "code_challenge_method", "S256");
        Add(parameters, "login_hint", loginHint);

        var authorization = endpoints.AuthorizationEndpoint.AbsoluteUri;
        var separator = string.IsNullOrEmpty(endpoints.AuthorizationEndpoint.Query) ? "?" : "&";
        return new Uri(authorization + separator + EncodeQuery(parameters));
    }

    public Task<OAuthTokenResult> ExchangeCodeAsync(
        Uri tokenEndpoint,
        string clientId,
        string code,
        string codeVerifier,
        Uri redirectUri,
        string? resource,
        CancellationToken cancellationToken = default)
    {
        var form = new List<KeyValuePair<string, string>>();
        Add(form, "grant_type", "authorization_code");
        Add(form, "code", code);
        Add(form, "redirect_uri", redirectUri.AbsoluteUri);
        Add(form, "client_id", clientId);
        Add(form, "code_verifier", codeVerifier);
        Add(form, "resource", resource);
        return PostTokenRequestAsync(tokenEndpoint, form, cancellationToken);
    }

    public Task<OAuthTokenResult> RefreshAsync(
        Uri tokenEndpoint,
        string clientId,
        string refreshToken,
        string? resource,
        CancellationToken cancellationToken = default)
    {
        // No scope parameter: RFC 6749 keeps the original grant's scope when it is omitted.
        var form = new List<KeyValuePair<string, string>>();
        Add(form, "grant_type", "refresh_token");
        Add(form, "refresh_token", refreshToken);
        Add(form, "client_id", clientId);
        Add(form, "resource", resource);
        return PostTokenRequestAsync(tokenEndpoint, form, cancellationToken);
    }

    public async Task RevokeAsync(Uri revocationEndpoint, string clientId, string token, string tokenTypeHint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revocationEndpoint);
        RequireHttps(revocationEndpoint.AbsoluteUri, "revocation_endpoint");

        var form = new List<KeyValuePair<string, string>>();
        Add(form, "token", token);
        Add(form, "token_type_hint", tokenTypeHint);
        Add(form, "client_id", clientId);

        using var content = new FormUrlEncodedContent(form);
        using var response = await SendAsync(() => HttpClient.PostAsync(revocationEndpoint, content, cancellationToken), "revocation", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await ReadBodyAsync(response, "revocation", cancellationToken).ConfigureAwait(false);
            throw CreateErrorResponseException("Token revocation failed.", response.StatusCode, body, "revocation");
        }
    }

    internal static Uri RequireHttps(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new OAuthProtocolException($"The OAuth {name} must be an absolute HTTPS URL.", "invalid_endpoint", null, isTransient: false);
        }

        return uri;
    }

    /// <summary>
    /// RFC 8414 requires an identical issuer; a trailing slash is ignored on both sides because servers disagree
    /// on it and it changes neither the origin nor the path.
    /// </summary>
    internal static bool IssuerEquals(string? documentIssuer, string requestedIssuer)
        => !string.IsNullOrWhiteSpace(documentIssuer)
           && string.Equals(documentIssuer.Trim().TrimEnd('/'), requestedIssuer.Trim().TrimEnd('/'), StringComparison.Ordinal);

    private static OAuthEndpoints BuildEndpoints(
        string issuer,
        string? authorizationEndpoint,
        string? tokenEndpoint,
        string? revocationEndpoint,
        string? registrationEndpoint,
        OAuthServerMetadata? metadata,
        OAuthTrustSource trustSource)
    {
        var issuerUri = RequireHttps(issuer, "issuer");
        var authorization = RequireHttps(authorizationEndpoint, "authorization_endpoint");
        var token = RequireHttps(tokenEndpoint, "token_endpoint");
        var revocation = string.IsNullOrWhiteSpace(revocationEndpoint) ? null : RequireHttps(revocationEndpoint, "revocation_endpoint");
        var registration = string.IsNullOrWhiteSpace(registrationEndpoint) ? null : RequireHttps(registrationEndpoint, "registration_endpoint");

        // Discovered and user-entered metadata is server-controlled; pinning every endpoint to the issuer's host keeps a
        // tampered document from sending the code, verifier or tokens elsewhere. Catalog entries may split hosts.
        if (trustSource != OAuthTrustSource.BuiltIn)
        {
            EnsureSameHost(issuerUri, authorization, "authorization_endpoint");
            EnsureSameHost(issuerUri, token, "token_endpoint");
            if (revocation is not null) EnsureSameHost(issuerUri, revocation, "revocation_endpoint");
            if (registration is not null) EnsureSameHost(issuerUri, registration, "registration_endpoint");
        }

        return new OAuthEndpoints(issuer.Trim(), authorization, token, revocation, registration, metadata);
    }

    private static void EnsureSameHost(Uri issuer, Uri endpoint, string name)
    {
        if (!string.Equals(issuer.IdnHost, endpoint.IdnHost, StringComparison.OrdinalIgnoreCase))
            throw new OAuthProtocolException($"The OAuth {name} is not on the issuer's host.", "endpoint_host_mismatch", null, isTransient: false);
    }

    private static IEnumerable<Uri> GetMetadataUrls(Uri issuer)
    {
        var path = issuer.AbsolutePath.TrimEnd('/');
        var origin = issuer.GetLeftPart(UriPartial.Authority);

        // RFC 8414 inserts the well-known segment between the host and the issuer's path; OIDC appends it.
        yield return new Uri(origin + OAuthWellKnownPath + path);
        yield return new Uri(origin + path + OpenIdWellKnownPath);
    }

    private static async Task<OAuthServerMetadata?> FetchMetadataDocumentAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => HttpClient.GetAsync(url, cancellationToken), "metadata", cancellationToken).ConfigureAwait(false);

        if ((int)response.StatusCode >= 500)
            throw new OAuthProtocolException("The metadata endpoint is unavailable.", null, (int)response.StatusCode, isTransient: true);

        if (!response.IsSuccessStatusCode)
            return null;

        var body = await ReadBodyAsync(response, "metadata", cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize(body, OAuthJsonContext.Default.OAuthServerMetadata);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<OAuthTokenResult> PostTokenRequestAsync(Uri tokenEndpoint, List<KeyValuePair<string, string>> form, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokenEndpoint);
        RequireHttps(tokenEndpoint.AbsoluteUri, "token_endpoint");

        using var content = new FormUrlEncodedContent(form);
        using var response = await SendAsync(() => HttpClient.PostAsync(tokenEndpoint, content, cancellationToken), "token", cancellationToken).ConfigureAwait(false);
        var body = await ReadBodyAsync(response, "token", cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            throw CreateErrorResponseException("The token request failed.", response.StatusCode, body, "token");

        OAuthTokenResponse? token;
        try
        {
            token = JsonSerializer.Deserialize(body, OAuthJsonContext.Default.OAuthTokenResponse);
        }
        catch (JsonException ex)
        {
            // A 200 that is not JSON is usually a captive portal or proxy page.
            throw new OAuthProtocolException("The token response is not valid JSON.", null, (int)response.StatusCode, isTransient: true, ex);
        }

        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
            throw new OAuthProtocolException("The token response has no access_token.", null, (int)response.StatusCode, isTransient: true);

        if (!string.IsNullOrWhiteSpace(token.TokenType) && !string.Equals(token.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new OAuthProtocolException("The token endpoint returned an unsupported token type.", "unsupported_token_type", (int)response.StatusCode, isTransient: false);

        return new OAuthTokenResult(
            token.AccessToken,
            string.IsNullOrWhiteSpace(token.RefreshToken) ? null : token.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(ParseLifetimeSeconds(token.ExpiresIn)),
            string.IsNullOrWhiteSpace(token.Scope) ? null : token.Scope);
    }

    internal static int ParseLifetimeSeconds(JsonElement expiresIn)
    {
        long seconds = 0;
        if (expiresIn.ValueKind == JsonValueKind.Number && expiresIn.TryGetInt64(out var number))
            seconds = number;
        else if (expiresIn.ValueKind == JsonValueKind.String
                 && long.TryParse(expiresIn.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            seconds = parsed;

        if (seconds <= 0)
            seconds = DefaultLifetimeSeconds;

        return (int)Math.Clamp(seconds, MinimumLifetimeSeconds, MaximumLifetimeSeconds);
    }

    private static async Task<HttpResponseMessage> SendAsync(Func<Task<HttpResponseMessage>> send, string operation, CancellationToken cancellationToken)
    {
        try
        {
            return await send().ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            Logger.Information("OAuth {Operation} request could not reach the server ({ErrorType}).", operation, ex.HttpRequestError);
            throw new OAuthProtocolException($"The OAuth {operation} request could not reach the server.", null, null, isTransient: true, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            Logger.Information("OAuth {Operation} request timed out.", operation);
            throw new OAuthProtocolException($"The OAuth {operation} request timed out.", null, null, isTransient: true, ex);
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new OAuthProtocolException($"The OAuth {operation} response could not be read.", null, (int)response.StatusCode, isTransient: true, ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OAuthProtocolException($"The OAuth {operation} response timed out.", null, (int)response.StatusCode, isTransient: true, ex);
        }
    }

    private static OAuthProtocolException CreateErrorResponseException(string message, HttpStatusCode statusCode, string body, string operation)
    {
        var status = (int)statusCode;
        var error = TryReadOAuthError(body);

        // Only the status and the RFC 6749 error code are logged; bodies can carry tokens or personal data.
        Logger.Warning("OAuth {Operation} request failed with {StatusCode} {OAuthError}.", operation, status, error ?? "no_oauth_error");

        var transient = status >= 500
                        || error is null
                        || error is "temporarily_unavailable" or "server_error";
        return new OAuthProtocolException(message, error, status, transient);
    }

    private static string? TryReadOAuthError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            var error = JsonSerializer.Deserialize(body, OAuthJsonContext.Default.OAuthErrorResponse)?.Error;
            return string.IsNullOrWhiteSpace(error) || error.Length > 64 || !error.All(IsErrorCodeCharacter) ? null : error;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // RFC 6749 limits error codes to printable ASCII without quote or backslash; anything else is not logged.
    private static bool IsErrorCodeCharacter(char value) => value is >= '\x20' and <= '\x7E' and not '"' and not '\\';

    private static string? Prefer(string? explicitValue, string? fallback)
        => string.IsNullOrWhiteSpace(explicitValue) ? fallback : explicitValue;

    private static void Add(List<KeyValuePair<string, string>> parameters, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            parameters.Add(new KeyValuePair<string, string>(name, value));
    }

    private static string EncodeQuery(IEnumerable<KeyValuePair<string, string>> parameters)
        => string.Join("&", parameters.Select(static pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
}
