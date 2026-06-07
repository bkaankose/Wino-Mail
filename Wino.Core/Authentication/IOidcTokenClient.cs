using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Authentication;

/// <summary>
/// Generic, provider-agnostic OpenID Connect / OAuth2 authorization-code + PKCE client.
/// Knows nothing about Exchange, EWS, or any UI; it only speaks the OIDC wire protocol
/// (discovery, PKCE, code exchange, refresh) so it can serve ExSTS, real AD FS, or any
/// standards-compliant issuer purely from configuration.
/// </summary>
public interface IOidcTokenClient
{
    /// <summary>
    /// Fetches the issuer's <c>.well-known/openid-configuration</c> document.
    /// </summary>
    Task<OidcDiscoveryDocument> GetDiscoveryDocumentAsync(string authority, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a fresh PKCE verifier/challenge pair (S256).
    /// </summary>
    PkcePair CreatePkcePair();

    /// <summary>
    /// Builds the interactive authorization-endpoint URL the user's browser is sent to.
    /// </summary>
    string BuildAuthorizationUrl(OidcDiscoveryDocument discovery, OidcConfiguration configuration, string codeChallenge, string state);

    /// <summary>
    /// Exchanges an authorization code (returned to the redirect URI) for an access/refresh token set.
    /// </summary>
    Task<OidcTokenSet> ExchangeAuthorizationCodeAsync(OidcDiscoveryDocument discovery, OidcConfiguration configuration, string code, string codeVerifier, CancellationToken cancellationToken = default);

    /// <summary>
    /// Redeems a refresh token for a new access token (and, when rotated, a new refresh token).
    /// </summary>
    Task<OidcTokenSet> RefreshAsync(OidcDiscoveryDocument discovery, OidcConfiguration configuration, string refreshToken, CancellationToken cancellationToken = default);
}

/// <summary>
/// The subset of the OIDC discovery document Wino needs to drive an auth-code + PKCE flow.
/// </summary>
public sealed class OidcDiscoveryDocument
{
    public string Issuer { get; init; }
    public string AuthorizationEndpoint { get; init; }
    public string TokenEndpoint { get; init; }
}

/// <summary>
/// Per-account OAuth configuration. For ExSTS these are pre-filled defaults; for real AD FS
/// the admin supplies the authority. Stored alongside the account's custom server information.
/// </summary>
public sealed class OidcConfiguration
{
    /// <summary>Base authority URL, e.g. <c>https://wsfed.mtec360.com/adfs</c>.</summary>
    public string Authority { get; init; }

    /// <summary>OAuth client id. For EWS this is the well-known <c>00000002-0000-0ff1-ce00-000000000000</c>.</summary>
    public string ClientId { get; init; }

    /// <summary>Protected resource the token is requested for, e.g. <c>https://mail.mtec360.com/</c>.</summary>
    public string Resource { get; init; }

    /// <summary>Redirect URI registered with the issuer (must be exact-match whitelisted).</summary>
    public string RedirectUri { get; init; }

    /// <summary>Requested scopes. <c>offline_access</c> is required to receive a refresh token.</summary>
    public string Scope { get; init; } = "openid profile offline_access";
}

/// <summary>
/// An access/refresh token pair plus the absolute UTC expiry of the access token.
/// </summary>
public sealed class OidcTokenSet
{
    public string AccessToken { get; init; }
    public string RefreshToken { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }

    /// <summary>
    /// True when the access token is present and not within <paramref name="skew"/> of expiring.
    /// </summary>
    public bool IsAccessTokenValid(TimeSpan skew)
        => !string.IsNullOrEmpty(AccessToken) && DateTimeOffset.UtcNow + skew < ExpiresAtUtc;
}

/// <summary>
/// A PKCE verifier (kept private to the client) and its S256 challenge (sent to the issuer).
/// </summary>
public readonly record struct PkcePair(string Verifier, string Challenge);

/// <summary>
/// Raised when the token endpoint returns a non-success response or a malformed payload.
/// </summary>
public sealed class OidcTokenException : Exception
{
    public OidcTokenException(string message) : base(message) { }
}
