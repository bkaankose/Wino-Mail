using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Exceptions;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Authentication;

namespace MailCal.Authentication;

public sealed class GmailAuthenticator : BaseAuthenticator, IGmailAuthenticator
{
    private static readonly HttpClient HttpClient = new();
    private static readonly ILogger Logger = Log.ForContext<GmailAuthenticator>();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> TokenLocks = new(StringComparer.Ordinal);
    private readonly MailCalGmailCodeReceiver _codeReceiver;
    private readonly IGoogleTokenStore _tokenStore;

    public GmailAuthenticator(
        IAuthenticatorConfig authConfig,
        IExternalLauncher externalLauncher,
        IExternalBrowserAuthenticationPresenter? authenticationPresenter,
        IGoogleTokenStore tokenStore) : base(authConfig)
    {
        ArgumentNullException.ThrowIfNull(externalLauncher);
        ArgumentNullException.ThrowIfNull(tokenStore);

        _codeReceiver = new MailCalGmailCodeReceiver(externalLauncher, authenticationPresenter, authConfig.ApplicationDisplayName);
        _tokenStore = tokenStore;
    }

    public string ClientId => AuthenticatorConfig.GmailAuthenticatorClientId;
    public override MailProviderType ProviderType => MailProviderType.Gmail;

    public async Task<TokenInformationEx> GenerateTokenInformationAsync(
        MailAccount account,
        IReadOnlyCollection<ProviderFeature> requestedFeatures = null, CancellationToken cancellationToken = default)
    {
        var credentialKey = GetCredentialKey(account);
        var tokenLock = GetTokenLock(credentialKey);
        await tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var storedToken = await AuthorizeInteractivelyAsync(account, credentialKey, requestedFeatures, cancellationToken).ConfigureAwait(false);
            return new TokenInformationEx(storedToken.AccessToken, account?.Address);
        }
        finally
        {
            tokenLock.Release();
        }
    }

    public async Task<TokenInformationEx> GetTokenInformationAsync(
        MailAccount account,
        IReadOnlyCollection<ProviderFeature> requiredFeatures = null, CancellationToken cancellationToken = default)
    {
        var credentialKey = GetCredentialKey(account);
        var tokenLock = GetTokenLock(credentialKey);
        await tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var storedToken = await ReadTokenAsync(credentialKey, cancellationToken).ConfigureAwait(false);

            if (storedToken == null)
            {
                throw new AuthenticationAttentionException(account);
            }
            else if (storedToken.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(5))
            {
                storedToken = await RefreshTokenAsync(account, storedToken, credentialKey, cancellationToken).ConfigureAwait(false);
            }

            return new TokenInformationEx(storedToken.AccessToken, account?.Address);
        }
        finally
        {
            tokenLock.Release();
        }
    }

    public async Task<TokenInformationEx> RefreshTokenInformationAsync(
        MailAccount account,
        IReadOnlyCollection<ProviderFeature> requiredFeatures = null, CancellationToken cancellationToken = default)
    {
        var credentialKey = GetCredentialKey(account);
        var tokenLock = GetTokenLock(credentialKey);
        await tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var storedToken = await ReadTokenAsync(credentialKey, cancellationToken).ConfigureAwait(false);

            if (storedToken == null)
            {
                throw new AuthenticationAttentionException(account);
            }

            storedToken = await RefreshTokenAsync(account, storedToken, credentialKey, cancellationToken).ConfigureAwait(false);
            return new TokenInformationEx(storedToken.AccessToken, account?.Address);
        }
        finally
        {
            tokenLock.Release();
        }
    }

    public async Task DeleteTokenInformationAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        var credentialKey = GetCredentialKey(account);
        var tokenLock = GetTokenLock(credentialKey);
        await tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _tokenStore.DeleteAsync(credentialKey, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private async Task<StoredGoogleToken> AuthorizeInteractivelyAsync(
        MailAccount account,
        string credentialKey,
        IReadOnlyCollection<ProviderFeature> requestedFeatures, CancellationToken cancellationToken)
    {
        var scopes = AuthenticatorConfig.GetGmailScopes(
            ProviderAuthorizationRequest.ForAccount(account, requestedFeatures));

        GoogleAuthorizationCode authorization;

        try
        {
            authorization = await _codeReceiver.ReceiveCodeAsync(
                (redirectUri, state) => BuildAuthorizationUri(redirectUri, state, scopes),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The user dismissed the in-app waiting dialog. Callers treat this like any other
            // interactive sign-in cancellation and back out silently.
            throw new AccountSetupCanceledException();
        }

        using var requestContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["code"] = authorization.Code,
            ["code_verifier"] = authorization.CodeVerifier,
            ["redirect_uri"] = authorization.RedirectUri.AbsoluteUri,
            ["grant_type"] = "authorization_code"
        });

        using var response = await HttpClient.PostAsync("https://oauth2.googleapis.com/token", requestContent, cancellationToken).ConfigureAwait(false);
        var tokenResponse = await ReadTokenResponseAsync(response, cancellationToken).ConfigureAwait(false);

        // Google's consent screen lets users untick individual permissions. Refuse a partial grant here
        // instead of storing a token that fails every later sync with "insufficient authentication scopes".
        var missingScopes = GetMissingScopes(scopes, tokenResponse.Scope);
        if (missingScopes.Count > 0)
        {
            Logger.Warning("Google sign-in for account {AccountId} did not grant {MissingScopes}.", account?.Id, missingScopes);
            throw new GoogleAuthenticationException(Translator.Exception_GoogleAuthMissingPermissions);
        }

        var previousToken = await ReadTokenAsync(credentialKey, cancellationToken).ConfigureAwait(false);
        var storedToken = CreateStoredToken(
            tokenResponse,
            string.IsNullOrWhiteSpace(tokenResponse.RefreshToken) ? previousToken?.RefreshToken : tokenResponse.RefreshToken,
            previousToken?.Scopes);
        await WriteTokenAsync(credentialKey, storedToken, cancellationToken).ConfigureAwait(false);
        return storedToken;
    }

    private async Task<StoredGoogleToken> RefreshTokenAsync(
        MailAccount account,
        StoredGoogleToken currentToken,
        string credentialKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentToken.RefreshToken))
        {
            throw new AuthenticationAttentionException(account);
        }

        using var requestContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["refresh_token"] = currentToken.RefreshToken,
            ["grant_type"] = "refresh_token"
        });

        using var response = await HttpClient.PostAsync("https://oauth2.googleapis.com/token", requestContent, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or
            System.Net.HttpStatusCode.Unauthorized or
            System.Net.HttpStatusCode.Forbidden)
        {
            // invalid_grant means the user revoked or Google expired the grant; anything else points at
            // the client registration. Only the OAuth error code is logged, never the response body.
            var errorCode = await ReadOAuthErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);
            Logger.Warning("Google rejected the refresh token for account {AccountId} with {StatusCode} {OAuthError}.",
                account?.Id, (int)response.StatusCode, errorCode);

            throw new AuthenticationAttentionException(account);
        }

        var tokenResponse = await ReadTokenResponseAsync(response, cancellationToken).ConfigureAwait(false);
        var storedToken = CreateStoredToken(tokenResponse, currentToken.RefreshToken, currentToken.Scopes);
        await WriteTokenAsync(credentialKey, storedToken, cancellationToken).ConfigureAwait(false);
        return storedToken;
    }

    private Uri BuildAuthorizationUri(Uri redirectUri, string state, IReadOnlyList<string> scopes)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["response_type"] = "code",
            ["scope"] = string.Join(" ", scopes),
            ["access_type"] = "offline",
            ["prompt"] = "consent",
            ["include_granted_scopes"] = "false",
            ["state"] = state
        };

        return new Uri($"https://accounts.google.com/o/oauth2/v2/auth?{BuildQueryString(query)}");
    }

    private static string BuildQueryString(IEnumerable<KeyValuePair<string, string>> values)
        => string.Join("&", System.Linq.Enumerable.Select(values,
            static pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    /// <summary>
    /// Requested scopes Google left out of the grant. Identity scopes cannot be declined and Google may
    /// report them as aliases, so they are not checked. A broader grant covers its narrower scopes.
    /// An empty grant list means Google did not report scopes, which cannot be verified.
    /// </summary>
    internal static IReadOnlyList<string> GetMissingScopes(IEnumerable<string> requestedScopes, string? grantedScopes)
    {
        if (string.IsNullOrWhiteSpace(grantedScopes))
            return [];

        var granted = new HashSet<string>(
            grantedScopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.Ordinal);

        return requestedScopes
            .Where(scope => !IsIdentityScope(scope) && !IsCoveredBy(scope, granted))
            .ToList();
    }

    private static bool IsIdentityScope(string scope)
        => scope is "openid" or "email" or "profile"
           || scope.StartsWith("https://www.googleapis.com/auth/userinfo.", StringComparison.Ordinal);

    private static bool IsCoveredBy(string scope, HashSet<string> granted)
    {
        if (granted.Contains(scope))
            return true;

        // Full mailbox access includes every narrower Gmail scope; full calendar access every calendar scope.
        if (scope.StartsWith("https://www.googleapis.com/auth/gmail.", StringComparison.Ordinal))
            return granted.Contains("https://mail.google.com/");

        if (scope.StartsWith("https://www.googleapis.com/auth/calendar.", StringComparison.Ordinal))
            return granted.Contains("https://www.googleapis.com/auth/calendar");

        return false;
    }

    private static async Task<string> ReadOAuthErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var error = JsonSerializer.Deserialize(content, GoogleOAuthJsonContext.Default.GoogleOAuthErrorResponse);
            return string.IsNullOrWhiteSpace(error?.Error) ? "unknown" : error.Error;
        }
        catch (JsonException)
        {
            return "unreadable";
        }
    }

    private static async Task<GoogleOAuthTokenResponse> ReadTokenResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Google OAuth token request failed ({(int)response.StatusCode}): {content}", null, response.StatusCode);
        }

        return JsonSerializer.Deserialize(content, GoogleOAuthJsonContext.Default.GoogleOAuthTokenResponse)
            ?? throw new InvalidOperationException("Google OAuth returned an empty token response.");
    }

    private static StoredGoogleToken CreateStoredToken(
        GoogleOAuthTokenResponse response,
        string refreshToken,
        IReadOnlyCollection<string> existingScopes = null)
        => new()
        {
            AccessToken = response.AccessToken,
            RefreshToken = string.IsNullOrWhiteSpace(response.RefreshToken) ? refreshToken : response.RefreshToken,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(response.ExpiresIn, 60)),
            Scopes = string.IsNullOrWhiteSpace(response.Scope)
                ? existingScopes?.ToList() ?? []
                : response.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
        };

    private async Task<StoredGoogleToken?> ReadTokenAsync(string credentialKey, CancellationToken cancellationToken)
    {
        var bytes = await _tokenStore.ReadAsync(credentialKey, cancellationToken).ConfigureAwait(false);
        if (bytes is null) return null;

        try
        {
            return JsonSerializer.Deserialize(bytes, GoogleOAuthJsonContext.Default.StoredGoogleToken)
                ?? throw new InvalidOperationException("The stored Google token is empty.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task WriteTokenAsync(string credentialKey, StoredGoogleToken token, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(token, GoogleOAuthJsonContext.Default.StoredGoogleToken);
        try
        {
            await _tokenStore.WriteAsync(credentialKey, bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
    private static string GetCredentialKey(MailAccount account)
        => account?.Id.ToString("N") ?? "default";

    private static SemaphoreSlim GetTokenLock(string credentialKey)
        => TokenLocks.GetOrAdd(credentialKey, static _ => new SemaphoreSlim(1, 1));
}

internal sealed class StoredGoogleToken
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public List<string> Scopes { get; set; } = [];
}

internal sealed class GoogleOAuthTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;
}

internal sealed class GoogleOAuthErrorResponse
{
    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;
}

[JsonSerializable(typeof(StoredGoogleToken))]
[JsonSerializable(typeof(GoogleOAuthTokenResponse))]
[JsonSerializable(typeof(GoogleOAuthErrorResponse))]
internal partial class GoogleOAuthJsonContext : JsonSerializerContext;
