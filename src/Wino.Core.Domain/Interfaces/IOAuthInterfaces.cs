#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Authorization;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Core.Domain.Interfaces;

/// <summary>Serialized OAuth token bytes per key. Missing returns null; failures propagate.</summary>
public interface IOAuthTokenStore
{
    Task<byte[]?> ReadAsync(string key, CancellationToken cancellationToken = default);
    Task WriteAsync(string key, byte[] serializedToken, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// Opens the system browser for an OAuth authorization request and returns the <see cref="OAuthRedirect.CustomSchemeRedirectUri"/>
/// callback. Kept apart from <see cref="IWinoAccountBrowserAuthBroker"/> so Wino Account sign-in never receives an OAuth callback.
/// Throws <see cref="OperationCanceledException"/> when the user closes the browser surface or the token is cancelled,
/// and <see cref="Exceptions.OAuthSignInException"/> with AnotherSignInInProgress when a browser sign-in is already waiting.
/// </summary>
public interface IOAuthBrowserBroker
{
    Task<Uri> AuthorizeAsync(Uri authorizationUri, CancellationToken cancellationToken);
}

/// <summary>
/// Obtains, stores and refreshes OAuth tokens. Keys are account ids; during setup the key is the
/// <see cref="OAuthSetupSession.Key"/>, whose tokens stay in memory until <see cref="PersistSessionAsync"/>.
/// </summary>
public interface IOAuthTokenService
{
    /// <summary>Runs the browser sign-in and keeps the result in memory only.</summary>
    Task<OAuthSetupSession> SignInAsync(AccountOAuthProfile profile, string? loginHint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-authenticates an existing account with its stored profile and replaces its stored tokens. Returns the profile
    /// actually used, which carries a newly registered client id when dynamic registration ran; callers save it.
    /// </summary>
    Task<AccountOAuthProfile> ReauthenticateAsync(Guid accountId, AccountOAuthProfile profile, string? loginHint, CancellationToken cancellationToken = default);

    /// <summary>Returns an access token valid for at least five more minutes.</summary>
    /// <exception cref="Exceptions.OAuthGrantInvalidException">Interactive sign-in is required.</exception>
    /// <exception cref="Exceptions.OAuthTransientException">The issuer is unavailable.</exception>
    Task<string> GetAccessTokenAsync(Guid key, CancellationToken cancellationToken = default);

    /// <summary>Forces a refresh after a server rejected a token that looked valid.</summary>
    Task<string> RefreshNowAsync(Guid key, CancellationToken cancellationToken = default);

    /// <summary>Writes the session's tokens under the new account id and forgets the session.</summary>
    Task PersistSessionAsync(OAuthSetupSession session, Guid accountId, CancellationToken cancellationToken = default);

    void DiscardSession(Guid sessionKey);

    /// <summary>Deletes stored tokens and calls the revocation endpoint when the profile has one. Best effort.</summary>
    Task SignOutAsync(Guid accountId, AccountOAuthProfile? profile, CancellationToken cancellationToken = default);

    Task DeleteTokensAsync(Guid accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The scopes the issuer granted to the setup session or stored token under <paramref name="key"/>. Empty when
    /// nothing is stored or the stored token cannot be read.
    /// </summary>
    Task<IReadOnlyCollection<string>> GetGrantedScopesAsync(Guid key, CancellationToken cancellationToken = default);
}

/// <summary>Finds an OAuth issuer and client for a mail server. Never throws for network failures.</summary>
public interface IOAuthIssuerDiscoveryService
{
    /// <param name="deferClientRegistration">
    /// Skip dynamic client registration and return the discovered profile without a client id;
    /// <see cref="IOAuthTokenService.SignInAsync"/> registers when the user starts the sign-in.
    /// </param>
    Task<OAuthDiscoveryResult> DiscoverAsync(
        string emailAddress,
        string imapHost,
        ImapAuthCapabilities capabilities,
        CancellationToken cancellationToken = default,
        bool deferClientRegistration = false);

    /// <summary>
    /// The scopes the profile's issuer supports: a built-in profile's declared scopes, otherwise the issuer metadata's
    /// scopes_supported. Empty when the metadata cannot be read.
    /// </summary>
    Task<IReadOnlyList<string>> GetSupportedScopesAsync(AccountOAuthProfile profile, CancellationToken cancellationToken = default);
}

/// <summary>Connects once to read the IMAP CAPABILITY list. Never throws for network failures.</summary>
public interface IImapAuthCapabilityProbe
{
    Task<ImapAuthCapabilities> ProbeAsync(CustomServerInformation serverInformation, CancellationToken cancellationToken = default);
}

public interface IAccountOAuthProfileService
{
    Task<AccountOAuthProfile?> GetAsync(Guid accountId);
    Task SaveAsync(AccountOAuthProfile profile);
    Task DeleteAsync(Guid accountId);
}

/// <summary>
/// Builds the bearer authentication CalDAV and CardDAV use when <see cref="DavOAuthRule"/>
/// applies, and null when DAV keeps its password. A rejected grant surfaces as a 401 <see cref="DavRequestException"/>,
/// an unreachable issuer as a network <see cref="DavRequestException"/>.
/// </summary>
public interface IDavOAuthAuthenticationProvider
{
    /// <summary>For a saved account; the token key is the account id and the issuer comes from its stored profile.</summary>
    Task<DavAuthenticationProfile?> TryCreateForAccountAsync(
        Guid accountId,
        CustomServerInformation? serverInformation,
        DavServiceKind kind,
        Uri? davUri,
        CancellationToken cancellationToken = default);

    /// <summary>For setup, where the token key is the in-memory session key and the profile is the session's.</summary>
    Task<DavAuthenticationProfile?> TryCreateAsync(
        Guid tokenKey,
        CustomServerInformation? serverInformation,
        AccountOAuthProfile? profile,
        DavServiceKind kind,
        Uri? davUri,
        CancellationToken cancellationToken = default);
}
