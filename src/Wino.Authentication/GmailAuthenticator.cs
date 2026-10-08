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
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Authentication;

public sealed class GmailAuthenticator : BaseAuthenticator, IGmailAuthenticator
{
    private static readonly HttpClient HttpClient = new();
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> TokenLocks = new(StringComparer.Ordinal);
    private readonly WinoGmailCodeReceiver _codeReceiver;
    private readonly IGoogleTokenStore _tokenStore;

    public GmailAuthenticator(
        IAuthenticatorConfig authConfig,
        IExternalLauncher externalLauncher,
        IExternalBrowserAuthenticationPresenter? authenticationPresenter,
        IGoogleTokenStore tokenStore) : base(authConfig)
    {
        ArgumentNullException.ThrowIfNull(externalLauncher);
        ArgumentNullException.ThrowIfNull(tokenStore);

        _codeReceiver = new WinoGmailCodeReceiver(externalLauncher, authenticationPresenter, authConfig.ApplicationDisplayName);
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

[JsonSerializable(typeof(StoredGoogleToken))]
[JsonSerializable(typeof(GoogleOAuthTokenResponse))]
internal partial class GoogleOAuthJsonContext : JsonSerializerContext;
