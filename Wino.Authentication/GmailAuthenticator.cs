using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Authentication;

namespace Wino.Authentication;

public sealed class GmailAuthenticator : BaseAuthenticator, IGmailAuthenticator
{
    private static readonly HttpClient HttpClient = new();
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

    public async Task<TokenInformationEx> GenerateTokenInformationAsync(MailAccount account)
    {
        await DeleteTokenInformationAsync(account).ConfigureAwait(false);
        return await GetTokenInformationAsync(account).ConfigureAwait(false);
    }

    public async Task<TokenInformationEx> GetTokenInformationAsync(MailAccount account)
    {
        var credentialKey = GetCredentialKey(account);
        var storedToken = await ReadTokenAsync(credentialKey).ConfigureAwait(false);

        if (storedToken == null)
        {
            storedToken = await AuthorizeInteractivelyAsync(account, credentialKey).ConfigureAwait(false);
        }
        else if (storedToken.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(5))
        {
            storedToken = await RefreshTokenAsync(storedToken, credentialKey).ConfigureAwait(false);
        }

        return new TokenInformationEx(storedToken.AccessToken, account?.Address);
    }

    public Task DeleteTokenInformationAsync(MailAccount account)
    {
        var tokenPath = GetTokenPath(GetCredentialKey(account));
        if (File.Exists(tokenPath))
        {
            File.Delete(tokenPath);
        }

        return Task.CompletedTask;
    }

    private async Task<StoredGoogleToken> AuthorizeInteractivelyAsync(MailAccount account, string credentialKey)
    {
        var scopes = AuthenticatorConfig.GetGmailScope(
            account?.IsMailAccessGranted != false,
            account?.IsCalendarAccessGranted == true);

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
        var storedToken = CreateStoredToken(tokenResponse, tokenResponse.RefreshToken);
        await WriteTokenAsync(credentialKey, storedToken).ConfigureAwait(false);
        return storedToken;
    }

    private async Task<StoredGoogleToken> RefreshTokenAsync(StoredGoogleToken currentToken, string credentialKey)
    {
        if (string.IsNullOrWhiteSpace(currentToken.RefreshToken))
        {
            return await AuthorizeInteractivelyAsync(null, credentialKey).ConfigureAwait(false);
        }

        using var requestContent = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["refresh_token"] = currentToken.RefreshToken,
            ["grant_type"] = "refresh_token"
        });

        using var response = await HttpClient.PostAsync("https://oauth2.googleapis.com/token", requestContent).ConfigureAwait(false);
        var tokenResponse = await ReadTokenResponseAsync(response).ConfigureAwait(false);
        var storedToken = CreateStoredToken(tokenResponse, currentToken.RefreshToken);
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

    private static StoredGoogleToken CreateStoredToken(GoogleOAuthTokenResponse response, string refreshToken)
        => new()
        {
            AccessToken = response.AccessToken,
            RefreshToken = string.IsNullOrWhiteSpace(response.RefreshToken) ? refreshToken : response.RefreshToken,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(response.ExpiresIn, 60))
        };

    private async Task<StoredGoogleToken?> ReadTokenAsync(string credentialKey)
    {
        var tokenPath = GetTokenPath(credentialKey);
        if (!File.Exists(tokenPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(tokenPath);
        return await JsonSerializer.DeserializeAsync(stream, GoogleOAuthJsonContext.Default.StoredGoogleToken).ConfigureAwait(false);
    }

    private async Task WriteTokenAsync(string credentialKey, StoredGoogleToken token)
    {
        Directory.CreateDirectory(_tokenStorePath);
        await using var stream = File.Create(GetTokenPath(credentialKey));
        await JsonSerializer.SerializeAsync(stream, token, GoogleOAuthJsonContext.Default.StoredGoogleToken).ConfigureAwait(false);
    }

    private string GetTokenPath(string credentialKey)
        => Path.Combine(_tokenStorePath, $"{credentialKey}.json");

    private static string GetCredentialKey(MailAccount account)
        => account?.Id.ToString("N") ?? "default";
}

internal sealed class StoredGoogleToken
{
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
}

internal sealed class GoogleOAuthTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
}

[JsonSerializable(typeof(StoredGoogleToken))]
[JsonSerializable(typeof(GoogleOAuthTokenResponse))]
internal partial class GoogleOAuthJsonContext : JsonSerializerContext;
