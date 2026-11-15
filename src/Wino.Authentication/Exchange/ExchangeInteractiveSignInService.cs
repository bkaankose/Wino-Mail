#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Authentication.OAuth;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Authentication.Exchange;

/// <summary>
/// Exchange modern-auth sign-in through <see cref="IEmbeddedBrowserAuthBroker"/>. It shares only the stateless
/// metadata and authorization URL helpers of <see cref="IOAuthProtocolClient"/> with IMAP OAuth; it never uses the
/// system browser, a loopback listener, protocol activation or the generic OAuth token store.
/// </summary>
public sealed class ExchangeInteractiveSignInService : IExchangeInteractiveSignInService
{
    // offline_access is what makes ADFS issue a refresh token; without one the account could not stay signed in.
    internal const string Scopes = "openid profile offline_access";

    private static readonly ILogger Logger = Log.ForContext<ExchangeInteractiveSignInService>();

    private readonly IOAuthProtocolClient _protocolClient;
    private readonly IExchangeTokenStore _tokenStore;
    private readonly ExchangeTokenCache _tokenCache;
    private readonly Func<IEmbeddedBrowserAuthBroker?> _browserBroker;

    public ExchangeInteractiveSignInService(
        IOAuthProtocolClient protocolClient,
        IExchangeTokenStore tokenStore,
        ExchangeTokenCache tokenCache,
        Func<IEmbeddedBrowserAuthBroker?> browserBroker)
    {
        _protocolClient = protocolClient;
        _tokenStore = tokenStore;
        _tokenCache = tokenCache;
        _browserBroker = browserBroker;
    }

    public bool IsSupported => _browserBroker() is not null;

    public async Task<ExchangeSignInResult> SignInAsync(
        Guid accountId,
        CustomServerInformation serverInformation,
        string? loginHint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverInformation);

        var broker = _browserBroker()
            ?? throw new ExchangeSignInException(ExchangeSignInFailureKind.Unsupported, Translator.Exception_ExchangeSignInUnsupported);

        var authority = serverInformation.OAuthAuthority?.Trim();
        var clientId = serverInformation.OAuthClientId?.Trim();
        if (string.IsNullOrEmpty(authority)
            || string.IsNullOrEmpty(clientId)
            || !Uri.TryCreate(serverInformation.OAuthRedirectUri?.Trim(), UriKind.Absolute, out var redirectUri))
        {
            throw new ExchangeSignInException(ExchangeSignInFailureKind.NotConfigured, Translator.Exception_ExchangeSignInNotConfigured);
        }

        var endpoints = await ResolveEndpointsAsync(authority, cancellationToken).ConfigureAwait(false);
        var resource = ExchangeOAuthAuthenticator.GetResource(serverInformation);

        var state = CreateRandomValue(32);
        var nonce = CreateRandomValue(32);
        var codeVerifier = CreateRandomValue(48);
        var codeChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

        var authorizationUri = _protocolClient.BuildAuthorizationUri(
            endpoints,
            clientId,
            Scopes,
            resource,
            redirectUri,
            state,
            codeChallenge,
            string.IsNullOrWhiteSpace(loginHint) ? null : loginHint.Trim());
        authorizationUri = new Uri(authorizationUri.AbsoluteUri + "&nonce=" + Uri.EscapeDataString(nonce));

        var browserResult = await broker.AuthenticateAsync(authorizationUri, redirectUri, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var callback = browserResult.Status switch
        {
            EmbeddedBrowserAuthStatus.Success when browserResult.RedirectUri is not null => browserResult.RedirectUri,
            EmbeddedBrowserAuthStatus.Canceled => throw new AccountSetupCanceledException(),
            _ => throw new ExchangeSignInException(ExchangeSignInFailureKind.BrowserFailed, Translator.Exception_ExchangeSignInBrowserFailed)
        };

        var code = ReadAuthorizationCode(callback, state, endpoints.Issuer);

        ExchangeAuthorizationCodeTokens tokens;
        try
        {
            tokens = await ExchangeAuthorizationCodeClient
                .ExchangeAsync(endpoints.TokenEndpoint, clientId, code, codeVerifier, redirectUri, resource, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OAuthProtocolException ex)
        {
            Logger.Warning(ex, "The Exchange authorization code exchange failed ({OAuthError}, HTTP {StatusCode}).", ex.OAuthError, ex.StatusCode);
            throw ex.IsTransient
                ? new ExchangeSignInException(ExchangeSignInFailureKind.ServerUnavailable, Translator.Exception_ExchangeSignInServerUnavailable, ex)
                : new ExchangeSignInException(ExchangeSignInFailureKind.TokenRequestFailed, Translator.Exception_ExchangeSignInTokenFailed, ex);
        }

        if (string.IsNullOrEmpty(tokens.RefreshToken))
            throw new ExchangeSignInException(ExchangeSignInFailureKind.NoRefreshToken, Translator.Exception_ExchangeSignInNoRefreshToken);

        var identity = ReadIdentity(tokens.IdToken, nonce);

        // The code is spent, so the tokens are kept even if the caller gave up meanwhile.
        await _tokenStore.SaveRefreshTokenAsync(accountId, tokens.RefreshToken, CancellationToken.None).ConfigureAwait(false);
        _tokenCache.Set(accountId, new ExchangeAccessToken(tokens.AccessToken, tokens.ExpiresAtUtc));

        return identity;
    }

    private async Task<OAuthEndpoints> ResolveEndpointsAsync(string authority, CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await _protocolClient.GetMetadataAsync(authority, cancellationToken).ConfigureAwait(false);
            return _protocolClient.CreateEndpoints(authority, metadata, OAuthTrustSource.UserEntered);
        }
        catch (OAuthProtocolException ex)
        {
            Logger.Warning(ex, "Exchange sign-in could not use the metadata of {Authority} ({OAuthError}).", authority, ex.OAuthError);
            throw ex.IsTransient
                ? new ExchangeSignInException(ExchangeSignInFailureKind.ServerUnavailable, Translator.Exception_ExchangeSignInServerUnavailable, ex)
                : new ExchangeSignInException(ExchangeSignInFailureKind.DiscoveryFailed, Translator.Exception_ExchangeSignInDiscoveryFailed, ex);
        }
    }

    private static string ReadAuthorizationCode(Uri callback, string expectedState, string issuer)
    {
        var parameters = ParseQuery(callback.Query);
        parameters.TryGetValue("code", out var code);
        parameters.TryGetValue("error", out var error);
        parameters.TryGetValue("state", out var state);

        if (string.IsNullOrEmpty(state)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
        {
            Logger.Warning("The Exchange sign-in response did not carry the expected state.");
            throw Failed();
        }

        // RFC 9207: when the server names itself, it must be the issuer this sign-in started with.
        if (parameters.TryGetValue("iss", out var responseIssuer) && !OAuthProtocolClient.IssuerEquals(responseIssuer, issuer))
        {
            Logger.Warning("The Exchange sign-in response came from a different issuer.");
            throw Failed();
        }

        if (!string.IsNullOrEmpty(error))
        {
            Logger.Warning("The Exchange sign-in returned the authorization error {OAuthError}.", error);
            if (string.Equals(error, "access_denied", StringComparison.Ordinal))
                throw new ExchangeSignInException(ExchangeSignInFailureKind.Denied, Translator.Exception_ExchangeSignInDenied);

            throw Failed();
        }

        if (string.IsNullOrEmpty(code))
            throw Failed();

        return code;
    }

    /// <summary>
    /// The identity in the ID token. Its signature is not checked: OpenID Connect Core 3.1.3.7 lets a client rely on
    /// the TLS connection to the token endpoint instead, and the claims only name the account; the refresh token is
    /// what grants access. The nonce is still checked so a replayed token is refused.
    /// </summary>
    private static ExchangeSignInResult ReadIdentity(string? idToken, string expectedNonce)
    {
        if (string.IsNullOrEmpty(idToken))
            return new ExchangeSignInResult(null, null);

        using var payload = ExchangeAuthorizationCodeClient.TryReadJwtPayload(idToken);
        if (payload is null)
            return new ExchangeSignInResult(null, null);

        var claims = payload.RootElement;
        var nonce = ExchangeAuthorizationCodeClient.GetString(claims, "nonce");
        if (nonce is not null && !string.Equals(nonce, expectedNonce, StringComparison.Ordinal))
        {
            Logger.Warning("The Exchange ID token carried a different nonce.");
            throw Failed();
        }

        var userName = FirstClaim(claims, "upn", "preferred_username", "email", "unique_name");
        var displayName = FirstClaim(claims, "name");
        return new ExchangeSignInResult(userName, displayName);
    }

    private static string? FirstClaim(JsonElement claims, params string[] names)
    {
        foreach (var name in names)
        {
            var value = ExchangeAuthorizationCodeClient.GetString(claims, name);
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }

    private static Dictionary<string, string> ParseQuery(string? query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query))
            return values;

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var key = Unescape(separator < 0 ? pair : pair[..separator]);
            var value = separator < 0 ? string.Empty : Unescape(pair[(separator + 1)..]);

            // A repeated parameter is ambiguous; refuse the response rather than pick one.
            if (!values.TryAdd(key, value))
                throw Failed();
        }

        return values;
    }

    private static string Unescape(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));

    private static ExchangeSignInException Failed()
        => new(ExchangeSignInFailureKind.Failed, Translator.Exception_ExchangeSignInFailed);

    private static string CreateRandomValue(int byteCount) => Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));

    private static string Base64UrlEncode(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
