using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
    private readonly string _tokenStorePath;

    public GmailAuthenticator(IAuthenticatorConfig authConfig, INativeAppService nativeAppService) : base(authConfig)
    {
        _codeReceiver = new WinoGmailCodeReceiver(nativeAppService);
        _tokenStorePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            authConfig.GmailTokenStoreIdentifier);
    }

    public string ClientId => AuthenticatorConfig.GmailAuthenticatorClientId;
    public bool ProposeCopyAuthURL { get; set; }
    public override MailProviderType ProviderType => MailProviderType.Gmail;

    public async Task<TokenInformationEx> GenerateTokenInformationAsync(
        MailAccount account,
        IReadOnlyCollection<ProviderFeature> requestedFeatures = null)
    {
        var credentialKey = GetCredentialKey(account);
        var tokenLock = GetTokenLock(credentialKey);
        await tokenLock.WaitAsync().ConfigureAwait(false);

        try
        {
            var storedToken = await AuthorizeInteractivelyAsync(account, credentialKey, requestedFeatures).ConfigureAwait(false);
            return new TokenInformationEx(storedToken.AccessToken, account?.Address);
        }
        finally
        {
            tokenLock.Release();
        }
    }

    public async Task<TokenInformationEx> GetTokenInformationAsync(
        MailAccount account,
        IReadOnlyCollection<ProviderFeature> requiredFeatures = null)
    {
        var credentialKey = GetCredentialKey(account);
        var tokenLock = GetTokenLock(credentialKey);
        await tokenLock.WaitAsync().ConfigureAwait(false);

        try
        {
            var storedToken = await ReadTokenAsync(credentialKey).ConfigureAwait(false);

            if (storedToken == null)
            {
                throw new AuthenticationAttentionException(account);
            }
            else if (storedToken.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(5))
            {
                storedToken = await RefreshTokenAsync(account, storedToken, credentialKey).ConfigureAwait(false);
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
        IReadOnlyCollection<ProviderFeature> requiredFeatures = null)
    {
        var credentialKey = GetCredentialKey(account);
        var tokenLock = GetTokenLock(credentialKey);
        await tokenLock.WaitAsync().ConfigureAwait(false);

        try
        {
            var storedToken = await ReadTokenAsync(credentialKey).ConfigureAwait(false);

            if (storedToken == null)
            {
                throw new AuthenticationAttentionException(account);
            }

            storedToken = await RefreshTokenAsync(account, storedToken, credentialKey).ConfigureAwait(false);
            return new TokenInformationEx(storedToken.AccessToken, account?.Address);
        }
        finally
        {
            tokenLock.Release();
        }
    }

    public async Task DeleteTokenInformationAsync(MailAccount account)
    {
        var credentialKey = GetCredentialKey(account);
        var tokenLock = GetTokenLock(credentialKey);
        await tokenLock.WaitAsync().ConfigureAwait(false);

        try
        {
            var tokenPath = GetTokenPath(credentialKey);
            if (File.Exists(tokenPath))
            {
                File.Delete(tokenPath);
            }
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private async Task<StoredGoogleToken> AuthorizeInteractivelyAsync(
        MailAccount account,
        string credentialKey,
        IReadOnlyCollection<ProviderFeature> requestedFeatures)
    {
        var scopes = AuthenticatorConfig.GetGmailScopes(
            ProviderAuthorizationRequest.ForAccount(account, requestedFeatures));

        var authorization = await _codeReceiver.ReceiveCodeAsync(
            (redirectUri, state) => BuildAuthorizationUri(redirectUri, state, scopes),
            ProposeCopyAuthURL,
            CancellationToken.None).ConfigureAwait(false);

        ProposeCopyAuthURL = false;

        using var requestContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["code"] = authorization.Code,
            ["code_verifier"] = authorization.CodeVerifier,
            ["redirect_uri"] = authorization.RedirectUri.AbsoluteUri,
            ["grant_type"] = "authorization_code"
        });

        using var response = await HttpClient.PostAsync("https://oauth2.googleapis.com/token", requestContent).ConfigureAwait(false);
        var tokenResponse = await ReadTokenResponseAsync(response).ConfigureAwait(false);
        var previousToken = await ReadTokenAsync(credentialKey).ConfigureAwait(false);
        var storedToken = CreateStoredToken(
            tokenResponse,
            string.IsNullOrWhiteSpace(tokenResponse.RefreshToken) ? previousToken?.RefreshToken : tokenResponse.RefreshToken,
            previousToken?.Scopes);
        await WriteTokenAsync(credentialKey, storedToken).ConfigureAwait(false);
        return storedToken;
    }

    private async Task<StoredGoogleToken> RefreshTokenAsync(
        MailAccount account,
        StoredGoogleToken currentToken,
        string credentialKey)
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

        using var response = await HttpClient.PostAsync("https://oauth2.googleapis.com/token", requestContent).ConfigureAwait(false);

        if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or
            System.Net.HttpStatusCode.Unauthorized or
            System.Net.HttpStatusCode.Forbidden)
        {
            throw new AuthenticationAttentionException(account);
        }

        var tokenResponse = await ReadTokenResponseAsync(response).ConfigureAwait(false);
        var storedToken = CreateStoredToken(tokenResponse, currentToken.RefreshToken, currentToken.Scopes);
        await WriteTokenAsync(credentialKey, storedToken).ConfigureAwait(false);
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
            ["include_granted_scopes"] = "true",
            ["state"] = state
        };

        return new Uri($"https://accounts.google.com/o/oauth2/v2/auth?{BuildQueryString(query)}");
    }

    private static string BuildQueryString(IEnumerable<KeyValuePair<string, string>> values)
        => string.Join("&", System.Linq.Enumerable.Select(values,
            static pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private static async Task<GoogleOAuthTokenResponse> ReadTokenResponseAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
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

    private async Task<StoredGoogleToken?> ReadTokenAsync(string credentialKey)
    {
        var tokenPath = GetTokenPath(credentialKey);
        if (!File.Exists(tokenPath))
        {
            return null;
        }

        await using var stream = new FileStream(
            tokenPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);
        return await JsonSerializer.DeserializeAsync(stream, GoogleOAuthJsonContext.Default.StoredGoogleToken).ConfigureAwait(false);
    }

    private async Task WriteTokenAsync(string credentialKey, StoredGoogleToken token)
    {
        Directory.CreateDirectory(_tokenStorePath);
        var tokenPath = GetTokenPath(credentialKey);
        var temporaryPath = Path.Combine(_tokenStorePath, $".{credentialKey}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer
                    .SerializeAsync(stream, token, GoogleOAuthJsonContext.Default.StoredGoogleToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }

            File.Move(temporaryPath, tokenPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string GetTokenPath(string credentialKey)
        => Path.Combine(_tokenStorePath, $"{credentialKey}.json");

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
