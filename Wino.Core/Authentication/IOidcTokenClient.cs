using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wino.Core.Authentication;

/// <summary>Provider-agnostic OpenID Connect auth-code + PKCE client.</summary>
public interface IOidcTokenClient
{
    Task<OidcDiscoveryDocument> GetDiscoveryDocumentAsync(string authority, CancellationToken cancellationToken = default);

    PkcePair CreatePkcePair();

    string BuildAuthorizationUrl(OidcDiscoveryDocument discovery, OidcConfiguration configuration, string codeChallenge, string state);

    Task<OidcTokenSet> ExchangeAuthorizationCodeAsync(OidcDiscoveryDocument discovery, OidcConfiguration configuration, string code, string codeVerifier, CancellationToken cancellationToken = default);

    Task<OidcTokenSet> RefreshAsync(OidcDiscoveryDocument discovery, OidcConfiguration configuration, string refreshToken, CancellationToken cancellationToken = default);
}

public sealed class OidcDiscoveryDocument
{
    public string Issuer { get; init; }
    public string AuthorizationEndpoint { get; init; }
    public string TokenEndpoint { get; init; }
}

public sealed class OidcConfiguration
{
    /// <summary>Base authority URL, e.g. <c>https://adfs.example.com/adfs</c>.</summary>
    public string Authority { get; init; }

    public string ClientId { get; init; }

    /// <summary>Protected resource the token is requested for, e.g. <c>https://mail.example.com/</c>.</summary>
    public string Resource { get; init; }

    public string RedirectUri { get; init; }

    /// <summary><c>offline_access</c> is required to receive a refresh token.</summary>
    public string Scope { get; init; } = "openid profile offline_access";
}

public sealed class OidcTokenSet
{
    public string AccessToken { get; init; }
    public string RefreshToken { get; init; }
    public DateTimeOffset ExpiresAtUtc { get; init; }

    public bool IsAccessTokenValid(TimeSpan skew)
        => !string.IsNullOrEmpty(AccessToken) && DateTimeOffset.UtcNow + skew < ExpiresAtUtc;
}

public readonly record struct PkcePair(string Verifier, string Challenge);

public sealed class OidcTokenException : Exception
{
    public OidcTokenException(string message) : base(message) { }
}
