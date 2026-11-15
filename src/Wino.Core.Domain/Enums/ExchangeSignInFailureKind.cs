namespace Wino.Core.Domain.Enums;

public enum ExchangeSignInFailureKind
{
    /// <summary>This device has no embedded browser for the organization's sign-in page.</summary>
    Unsupported,

    /// <summary>The account lacks the OAuth authority, client id or redirect address.</summary>
    NotConfigured,

    /// <summary>The identity provider could not be reached or answered without an OAuth error.</summary>
    ServerUnavailable,

    /// <summary>The identity provider's metadata is missing or its endpoints failed the HTTPS and host checks.</summary>
    DiscoveryFailed,

    /// <summary>The embedded browser could not be shown.</summary>
    BrowserFailed,

    /// <summary>The user or the identity provider declined the authorization.</summary>
    Denied,

    /// <summary>The authorization response was an error or could not be verified.</summary>
    Failed,

    /// <summary>The token endpoint refused the authorization code.</summary>
    TokenRequestFailed,

    /// <summary>The token endpoint issued no refresh token, so the account could not stay signed in.</summary>
    NoRefreshToken
}
