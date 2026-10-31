#nullable enable
using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Authorization;

public static class OAuthRedirect
{
    /// <summary>
    /// Every head already routes host auth-complete; the path keeps OAuth callbacks away from Wino Account sign-in.
    /// </summary>
    public const string CallbackPath = "/oauth";

    public static readonly Uri CustomSchemeRedirectUri = new($"{Accounts.WinoAccountExternalAuth.CallbackScheme}://{Accounts.WinoAccountExternalAuth.CallbackHost}{CallbackPath}");

    public static bool IsOAuthCallback(Uri? uri)
        => Accounts.WinoAccountExternalAuth.IsCallback(uri)
           && uri is not null
           && string.Equals(uri.AbsolutePath.TrimEnd('/'), CallbackPath, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Sign-in result held in memory by account setup. Nothing in it reaches disk until the account is saved.
/// </summary>
public sealed class OAuthSetupSession
{
    public OAuthSetupSession(Guid key, AccountOAuthProfile profile)
    {
        Key = key;
        Profile = profile;
    }

    /// <summary>Becomes the new account's id when setup saves.</summary>
    public Guid Key { get; }

    public AccountOAuthProfile Profile { get; }

    public bool HasRefreshToken { get; init; }
}

public sealed class OAuthDiscoveryResult
{
    public static OAuthDiscoveryResult NotOffered { get; } = new();

    public AccountOAuthProfile? Profile { get; init; }

    /// <summary>The server advertised OAUTHBEARER or XOAUTH2 but Wino has no way to complete a sign-in.</summary>
    public bool OfferedWithoutClient { get; init; }

    /// <summary>The issuer is not under the mail host's registrable domain; the user must confirm its host first.</summary>
    public bool RequiresIssuerConfirmation { get; init; }

    public string? IssuerHost { get; init; }

    /// <summary>The server's scopes_supported, or a catalog entry's declared scopes. Empty when unknown.</summary>
    public IReadOnlyList<string> SupportedScopes { get; init; } = Array.Empty<string>();

    public bool CanSignIn => Profile is not null;
}

public sealed class ImapAuthCapabilities
{
    public static ImapAuthCapabilities Unknown { get; } = new();

    public bool Reached { get; init; }
    public bool IsTlsVerified { get; init; }
    public IReadOnlyList<string> Mechanisms { get; init; } = Array.Empty<string>();
    public bool SupportsOAuthBearer { get; init; }
    public bool SupportsXOAuth2 { get; init; }
    public bool SupportsOAuth => SupportsOAuthBearer || SupportsXOAuth2;

    public OAuthSaslMechanism PreferredOAuthMechanism
        => SupportsOAuthBearer ? OAuthSaslMechanism.OAuthBearer : OAuthSaslMechanism.XOAuth2;
}
