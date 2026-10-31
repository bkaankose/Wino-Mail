#nullable enable
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authorization;

namespace Wino.Authentication.OAuth;

/// <summary>
/// Obtains, stores and refreshes OAuth tokens for IMAP, SMTP and later Exchange. Register it as a singleton: setup
/// sessions and the per-key locks live in this instance, and every connection must share one lock per key.
/// </summary>
public sealed class OAuthTokenService : IOAuthTokenService
{
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RevocationTimeout = TimeSpan.FromSeconds(10);
    private static readonly ILogger Logger = Log.ForContext<OAuthTokenService>();

    private readonly IOAuthProtocolClient _protocol;
    private readonly IOAuthTokenStore _store;
    private readonly Func<IOAuthBrowserBroker?> _browserBroker;
    private readonly Func<IExternalLauncher?> _launcher;
    private readonly Func<IExternalBrowserAuthenticationPresenter?> _presenter;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<Guid, SetupSessionEntry> _sessions = new();

    /// <param name="browserBroker">Custom-scheme sign-in. Null on a head that has none.</param>
    /// <param name="launcher">Opens the browser for loopback sign-in. Null on a head that has none.</param>
    /// <param name="presenter">The optional waiting dialog shown during loopback sign-in.</param>
    public OAuthTokenService(
        IOAuthProtocolClient protocol,
        IOAuthTokenStore store,
        Func<IOAuthBrowserBroker?> browserBroker,
        Func<IExternalLauncher?> launcher,
        Func<IExternalBrowserAuthenticationPresenter?> presenter)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(browserBroker);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(presenter);

        _protocol = protocol;
        _store = store;
        _browserBroker = browserBroker;
        _launcher = launcher;
        _presenter = presenter;
    }

    /// <summary>
    /// The <see cref="IOAuthTokenStore"/> key for an account: the account id in "N" format. Stored items already
    /// use this name, so it must never change.
    /// </summary>
    public static string GetStoreKey(Guid accountId) => accountId.ToString("N");

    public async Task<OAuthSetupSession> SignInAsync(AccountOAuthProfile profile, string? loginHint, CancellationToken cancellationToken = default)
    {
        var (usedProfile, token) = await RunSignInAsync(profile, loginHint, cancellationToken).ConfigureAwait(false);
        var session = new OAuthSetupSession(Guid.NewGuid(), usedProfile)
        {
            HasRefreshToken = !string.IsNullOrWhiteSpace(token.RefreshToken)
        };

        _sessions[session.Key] = new SetupSessionEntry(session, token);
        return session;
    }

    public async Task<AccountOAuthProfile> ReauthenticateAsync(Guid accountId, AccountOAuthProfile profile, string? loginHint, CancellationToken cancellationToken = default)
    {
        var (usedProfile, token) = await RunSignInAsync(profile, loginHint, cancellationToken).ConfigureAwait(false);

        var tokenLock = GetLock(accountId);
        await tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.IsNullOrWhiteSpace(token.RefreshToken))
            {
                var previous = await TryReadStoredAsync(accountId, cancellationToken).ConfigureAwait(false);
                if (previous is not null && IsSameRegistration(previous, token))
                    token.RefreshToken = previous.RefreshToken;
            }

            await WriteStoredAsync(accountId, token).ConfigureAwait(false);
        }
        finally
        {
            tokenLock.Release();
        }

        return usedProfile;
    }

    public Task<string> GetAccessTokenAsync(Guid key, CancellationToken cancellationToken = default)
        => GetAccessTokenCoreAsync(key, forceRefresh: false, cancellationToken);

    public Task<string> RefreshNowAsync(Guid key, CancellationToken cancellationToken = default)
        => GetAccessTokenCoreAsync(key, forceRefresh: true, cancellationToken);

    public async Task PersistSessionAsync(OAuthSetupSession session, Guid accountId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var sessionLock = GetLock(session.Key);
        await sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_sessions.TryGetValue(session.Key, out var entry))
                throw new InvalidOperationException("The OAuth setup session no longer exists.");

            // Setup normally saves the account under the session key itself, whose lock is already held.
            var accountLock = accountId == session.Key ? null : GetLock(accountId);
            if (accountLock is not null)
                await accountLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await WriteStoredAsync(accountId, entry.Token).ConfigureAwait(false);
            }
            finally
            {
                accountLock?.Release();
            }

            _sessions.TryRemove(session.Key, out _);
        }
        finally
        {
            sessionLock.Release();
        }
    }

    public void DiscardSession(Guid sessionKey) => _sessions.TryRemove(sessionKey, out _);

    public async Task SignOutAsync(Guid accountId, AccountOAuthProfile? profile, CancellationToken cancellationToken = default)
    {
        OAuthStoredToken? token;
        var tokenLock = GetLock(accountId);
        await tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            token = await TryReadStoredAsync(accountId, cancellationToken).ConfigureAwait(false);
            await _store.DeleteAsync(GetStoreKey(accountId), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            tokenLock.Release();
        }

        if (token is not null)
            await TryRevokeAsync(accountId, token, profile, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteTokensAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var tokenLock = GetLock(accountId);
        await tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.DeleteAsync(GetStoreKey(accountId), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            tokenLock.Release();
        }
    }

    public async Task<System.Collections.Generic.IReadOnlyCollection<string>> GetGrantedScopesAsync(Guid key, CancellationToken cancellationToken = default)
    {
        var tokenLock = GetLock(key);
        await tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(key, out var session))
                return session.Token.Scopes?.ToArray() ?? [];

            var stored = await TryReadStoredAsync(key, cancellationToken).ConfigureAwait(false);
            return stored?.Scopes?.ToArray() ?? [];
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private async Task<string> GetAccessTokenCoreAsync(Guid key, bool forceRefresh, CancellationToken cancellationToken)
    {
        var tokenLock = GetLock(key);
        await tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(key, out var session))
            {
                if (!forceRefresh && IsFresh(session.Token))
                    return session.Token.AccessToken;

                session.Token = await RefreshAsync(key, session.Token, cancellationToken).ConfigureAwait(false);
                return session.Token.AccessToken;
            }

            // Read inside the lock: issuers that rotate refresh tokens void the previous one on every refresh.
            var stored = await ReadStoredAsync(key, cancellationToken).ConfigureAwait(false)
                ?? throw new OAuthGrantInvalidException(key, null, isConfigurationError: false);

            if (!forceRefresh && IsFresh(stored))
                return stored.AccessToken;

            var refreshed = await RefreshAsync(key, stored, cancellationToken).ConfigureAwait(false);

            // Written before the lock is released, so the next caller never presents the superseded refresh token.
            await WriteStoredAsync(key, refreshed).ConfigureAwait(false);
            return refreshed.AccessToken;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private async Task<OAuthStoredToken> RefreshAsync(Guid key, OAuthStoredToken current, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(current.RefreshToken))
            throw new OAuthGrantInvalidException(key, "no_refresh_token", isConfigurationError: false);

        Uri tokenEndpoint;
        try
        {
            tokenEndpoint = OAuthProtocolClient.RequireHttps(current.TokenEndpoint, "token_endpoint");
        }
        catch (OAuthProtocolException)
        {
            throw new OAuthGrantInvalidException(key, "invalid_endpoint", isConfigurationError: true);
        }

        OAuthTokenResult result;
        try
        {
            result = await _protocol.RefreshAsync(tokenEndpoint, current.ClientId, current.RefreshToken, current.Resource, cancellationToken).ConfigureAwait(false);
        }
        catch (OAuthProtocolException ex) when (ex.IsTransient)
        {
            throw new OAuthTransientException("The OAuth issuer is unavailable.", ex);
        }
        catch (OAuthProtocolException ex)
        {
            var isConfigurationError = ex.OAuthError != "invalid_grant";
            if (isConfigurationError)
                Logger.Error("OAuth refresh for {Key} failed with a configuration error {OAuthError}; the client registration may be broken or withdrawn.", key, ex.OAuthError);
            else
                Logger.Warning("OAuth refresh for {Key} failed with {OAuthError}; interactive sign-in is required.", key, ex.OAuthError);

            throw new OAuthGrantInvalidException(key, ex.OAuthError, isConfigurationError);
        }

        return new OAuthStoredToken
        {
            Issuer = current.Issuer,
            ClientId = current.ClientId,
            TokenEndpoint = current.TokenEndpoint,
            RevocationEndpoint = current.RevocationEndpoint,
            Resource = current.Resource,
            AccessToken = result.AccessToken,
            RefreshToken = result.RefreshToken ?? current.RefreshToken,
            ExpiresAtUtc = result.ExpiresAtUtc,
            Scopes = result.Scope is null ? current.Scopes : SplitScopes(result.Scope)
        };
    }

    private async Task<(AccountOAuthProfile Profile, OAuthStoredToken Token)> RunSignInAsync(
        AccountOAuthProfile profile,
        string? loginHint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        OAuthEndpoints endpoints;
        try
        {
            endpoints = await _protocol.ResolveEndpointsAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        catch (OAuthProtocolException ex)
        {
            throw new OAuthSignInException(OAuthSignInFailureKind.Failed, "The sign-in server's settings could not be loaded.", ex);
        }

        var usedProfile = profile.Clone();
        if (string.IsNullOrWhiteSpace(usedProfile.RevocationEndpoint) && endpoints.RevocationEndpoint is not null)
            usedProfile.RevocationEndpoint = endpoints.RevocationEndpoint.AbsoluteUri;

        if (string.IsNullOrWhiteSpace(usedProfile.ClientId))
        {
            var registration = await RegisterClientAsync(usedProfile, endpoints, cancellationToken).ConfigureAwait(false);
            usedProfile.ClientId = registration.ClientId;
            usedProfile.RedirectKind = registration.RedirectKind;
        }

        var codeVerifier = OAuthPkce.CreateCodeVerifier();
        var codeChallenge = OAuthPkce.CreateChallenge(codeVerifier);
        var state = OAuthPkce.CreateState();
        var scopes = string.IsNullOrWhiteSpace(usedProfile.Scopes) ? null : usedProfile.Scopes.Trim();
        var resource = string.IsNullOrWhiteSpace(usedProfile.Resource) ? null : usedProfile.Resource.Trim();

        Uri BuildAuthorizationUri(Uri redirectUri)
            => _protocol.BuildAuthorizationUri(endpoints, usedProfile.ClientId, scopes, resource, redirectUri, state, codeChallenge, loginHint);

        string code;
        Uri usedRedirectUri;
        var issuerHost = endpoints.AuthorizationEndpoint.Host;

        if (usedProfile.RedirectKind == OAuthRedirectKind.Loopback)
        {
            var launcher = _launcher()
                ?? throw new OAuthSignInException(OAuthSignInFailureKind.Failed, "This device cannot open a browser for sign-in.");
            var receiver = new OAuthLoopbackReceiver(launcher, _presenter());
            var received = await receiver.ReceiveAsync(BuildAuthorizationUri, state, endpoints.Issuer, issuerHost,
                usedProfile.LoopbackPort, SignInTimeout, cancellationToken).ConfigureAwait(false);
            code = received.Code;
            usedRedirectUri = received.RedirectUri;
        }
        else
        {
            usedRedirectUri = OAuthRedirect.CustomSchemeRedirectUri;
            code = await ReceiveCustomSchemeCodeAsync(BuildAuthorizationUri(usedRedirectUri), state, endpoints.Issuer, cancellationToken).ConfigureAwait(false);
        }

        OAuthTokenResult result;
        try
        {
            result = await _protocol.ExchangeCodeAsync(endpoints.TokenEndpoint, usedProfile.ClientId, code, codeVerifier, usedRedirectUri, resource, cancellationToken).ConfigureAwait(false);
        }
        catch (OAuthProtocolException ex)
        {
            Logger.Warning("OAuth code exchange with {IssuerHost} failed with {OAuthError}.", issuerHost, ex.OAuthError ?? "no_oauth_error");
            throw new OAuthSignInException(OAuthSignInFailureKind.Failed, "The sign-in server did not issue a token.", ex);
        }

        var token = new OAuthStoredToken
        {
            Issuer = endpoints.Issuer,
            ClientId = usedProfile.ClientId,
            TokenEndpoint = endpoints.TokenEndpoint.AbsoluteUri,
            RevocationEndpoint = endpoints.RevocationEndpoint?.AbsoluteUri,
            Resource = resource,
            AccessToken = result.AccessToken,
            RefreshToken = result.RefreshToken,
            ExpiresAtUtc = result.ExpiresAtUtc,
            Scopes = SplitScopes(result.Scope ?? scopes)
        };

        if (string.IsNullOrWhiteSpace(token.RefreshToken))
            Logger.Information("OAuth sign-in with {IssuerHost} returned no refresh token.", issuerHost);

        return (usedProfile, token);
    }

    private async Task<OAuthClientRegistration> RegisterClientAsync(AccountOAuthProfile profile, OAuthEndpoints endpoints, CancellationToken cancellationToken)
    {
        if (profile.TrustSource == OAuthTrustSource.BuiltIn || endpoints.RegistrationEndpoint is null)
            throw new OAuthSignInException(OAuthSignInFailureKind.Failed, "No client is registered for this sign-in server.");

        try
        {
            return await _protocol.RegisterClientAsync(endpoints.RegistrationEndpoint, profile.Scopes,
                profile.RedirectKind == OAuthRedirectKind.CustomScheme, cancellationToken).ConfigureAwait(false);
        }
        catch (OAuthProtocolException ex)
        {
            throw new OAuthSignInException(OAuthSignInFailureKind.Failed, "Wino Mail could not register with the sign-in server.", ex);
        }
    }

    private async Task<string> ReceiveCustomSchemeCodeAsync(Uri authorizationUri, string state, string issuer, CancellationToken cancellationToken)
    {
        var broker = _browserBroker()
            ?? throw new OAuthSignInException(OAuthSignInFailureKind.Failed, "This device cannot open a browser for sign-in.");

        using var timeout = new CancellationTokenSource(SignInTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        Uri callback;
        try
        {
            callback = await broker.AuthorizeAsync(authorizationUri, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            if (timeout.IsCancellationRequested)
                throw new OAuthSignInException(OAuthSignInFailureKind.TimedOut, "The sign-in timed out.", ex);

            throw new OAuthSignInException(OAuthSignInFailureKind.Cancelled, "The sign-in was cancelled.", ex);
        }
        catch (Exception ex) when (ex is not OAuthSignInException and not OperationCanceledException)
        {
            throw new OAuthSignInException(OAuthSignInFailureKind.Failed, "The browser sign-in could not start.", ex);
        }

        if (!OAuthRedirect.IsOAuthCallback(callback))
            throw new OAuthSignInException(OAuthSignInFailureKind.Failed, "The sign-in returned to an unexpected address.");

        return OAuthAuthorizationCallback.ReadCode(callback.Query, callback.Fragment.Length > 1, state, issuer);
    }

    private async Task TryRevokeAsync(Guid accountId, OAuthStoredToken token, AccountOAuthProfile? profile, CancellationToken cancellationToken)
    {
        var endpoint = !string.IsNullOrWhiteSpace(token.RevocationEndpoint) ? token.RevocationEndpoint : profile?.RevocationEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint))
            return;

        var (value, hint) = string.IsNullOrWhiteSpace(token.RefreshToken)
            ? (token.AccessToken, "access_token")
            : (token.RefreshToken, "refresh_token");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RevocationTimeout);
            var revocationUri = OAuthProtocolClient.RequireHttps(endpoint, "revocation_endpoint");
            await _protocol.RevokeAsync(revocationUri, token.ClientId, value, hint, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OAuthProtocolException or OperationCanceledException)
        {
            // Best effort: the local tokens are already gone and the grant expires on the issuer's side.
            Logger.Information("OAuth revocation for {AccountId} did not complete ({ErrorType}).", accountId, ex.GetType().Name);
        }
    }

    private static bool IsFresh(OAuthStoredToken token)
        => !string.IsNullOrWhiteSpace(token.AccessToken) && token.ExpiresAtUtc > DateTimeOffset.UtcNow.Add(RefreshMargin);

    private static bool IsSameRegistration(OAuthStoredToken left, OAuthStoredToken right)
        => string.Equals(left.ClientId, right.ClientId, StringComparison.Ordinal)
           && OAuthProtocolClient.IssuerEquals(left.Issuer, right.Issuer);

    private static System.Collections.Generic.List<string> SplitScopes(string? scopes)
        => string.IsNullOrWhiteSpace(scopes)
            ? []
            : scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private SemaphoreSlim GetLock(Guid key) => _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

    private async Task<OAuthStoredToken?> TryReadStoredAsync(Guid key, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadStoredAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (OAuthGrantInvalidException)
        {
            return null;
        }
    }

    private async Task<OAuthStoredToken?> ReadStoredAsync(Guid key, CancellationToken cancellationToken)
    {
        var bytes = await _store.ReadAsync(GetStoreKey(key), cancellationToken).ConfigureAwait(false);
        if (bytes is null)
            return null;

        try
        {
            var token = JsonSerializer.Deserialize(bytes, OAuthJsonContext.Default.OAuthStoredToken);
            if (token is null || string.IsNullOrWhiteSpace(token.ClientId) || string.IsNullOrWhiteSpace(token.TokenEndpoint))
                throw new JsonException("The stored OAuth token is incomplete.");

            return token;
        }
        catch (JsonException)
        {
            Logger.Warning("The stored OAuth token for {Key} could not be read.", key);
            throw new OAuthGrantInvalidException(key, "unreadable_token", isConfigurationError: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task WriteStoredAsync(Guid key, OAuthStoredToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(token, OAuthJsonContext.Default.OAuthStoredToken);
        try
        {
            // The write must not be abandoned halfway: a rotated refresh token that never reaches the store is lost.
            await _store.WriteAsync(GetStoreKey(key), bytes, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private sealed class SetupSessionEntry(OAuthSetupSession session, OAuthStoredToken token)
    {
        public OAuthSetupSession Session { get; } = session;

        /// <summary>Replaced only under the session key's lock.</summary>
        public OAuthStoredToken Token { get; set; } = token;
    }
}
