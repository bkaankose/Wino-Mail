#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Api.Contracts.Users;

namespace Wino.Services;

public sealed class WinoAccountApiClient : IWinoAccountApiClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IDatabaseService _databaseService;
    private readonly SemaphoreSlim _tokenRefreshLock = new(1, 1);
    private readonly bool _ownsHttpClient;

    // private const string ApiUrl = "https://localhost:7204/";
    private const string ApiUrl = "https://api.winomail.app/";

    public WinoAccountApiClient(IDatabaseService databaseService, HttpClient? httpClient = null)
    {
        _databaseService = databaseService;

        if (httpClient != null)
        {
            _httpClient = httpClient;
            return;
        }

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = ValidateCertificate
        };

        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(ApiUrl)
        };

        _ownsHttpClient = true;
    }

    public Task<WinoAccountApiResult<AuthResultDto>> RegisterAsync(string email, string password, CancellationToken cancellationToken = default)
        => SendAuthRequestAsync("api/v1/auth/register", new RegisterRequest(email, password), WinoAccountApiJsonContext.Default.RegisterRequest, cancellationToken);

    public Task<WinoAccountApiResult<AuthResultDto>> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
        => SendAuthRequestAsync("api/v1/auth/login", new LoginRequest(email, password), WinoAccountApiJsonContext.Default.LoginRequest, cancellationToken);

    public Task<WinoAccountApiResult<AuthResultDto>> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
        => SendAuthRequestAsync("api/v1/auth/refresh", new RefreshRequest(refreshToken), WinoAccountApiJsonContext.Default.RefreshRequest, cancellationToken);

    public Task<ApiEnvelope<EmailConfirmationResendResultDto>> ResendEmailConfirmationAsync(string endpoint, string ticket, CancellationToken cancellationToken = default)
        => SendAnonymousRequestAsync(
            HttpMethod.Post,
            endpoint,
            new ResendEmailConfirmationRequest(ticket),
            WinoAccountApiJsonContext.Default.ResendEmailConfirmationRequest,
            WinoAccountApiJsonContext.Default.ApiEnvelopeEmailConfirmationResendResultDto,
            cancellationToken);

    public Task<ApiEnvelope<JsonElement>> ForgotPasswordAsync(string email, CancellationToken cancellationToken = default)
        => SendAnonymousRequestAsync(
            HttpMethod.Post,
            "api/v1/auth/forgot-password",
            new ForgotPasswordRequest(email),
            WinoAccountApiJsonContext.Default.ForgotPasswordRequest,
            WinoAccountApiJsonContext.Default.ApiEnvelopeJsonElement,
            cancellationToken);

    public async Task<ApiEnvelope<JsonElement>> LogoutAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                "api/v1/auth/logout",
                new LogoutRequest(refreshToken),
                WinoAccountApiJsonContext.Default.LogoutRequest,
                cancellationToken).ConfigureAwait(false);

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var envelope = string.IsNullOrWhiteSpace(payload)
                ? null
                : JsonSerializer.Deserialize(payload, WinoAccountApiJsonContext.Default.ApiEnvelopeJsonElement);

            return envelope ?? ApiEnvelope<JsonElement>.Failure($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim());
        }
        catch (Exception ex)
        {
            return ApiEnvelope<JsonElement>.Failure(ex.Message);
        }
    }

    public Task<ApiEnvelope<AuthUserDto>> GetCurrentUserAsync(CancellationToken cancellationToken = default)
        => SendAuthorizedRequestAsync("api/v1/auth/me", WinoAccountApiJsonContext.Default.ApiEnvelopeAuthUserDto, cancellationToken);

    public Task<ApiEnvelope<AiStatusResultDto>> GetAiStatusAsync(CancellationToken cancellationToken = default)
        => SendAuthorizedRequestAsync("api/v1/ai/status", WinoAccountApiJsonContext.Default.ApiEnvelopeAiStatusResultDto, cancellationToken);

    public Task<ApiEnvelope<AiTextResultDto>> SummarizeAsync(string html, string targetLanguage, CancellationToken cancellationToken = default)
        => SendAuthorizedRequestAsync(
            HttpMethod.Post,
            "api/v1/ai/summarize",
            new SummarizeRequest(html, targetLanguage),
            WinoAccountApiJsonContext.Default.SummarizeRequest,
            WinoAccountApiJsonContext.Default.ApiEnvelopeAiTextResultDto,
            cancellationToken);

    public Task<ApiEnvelope<AiTextResultDto>> TranslateAsync(string html, string targetLanguage, CancellationToken cancellationToken = default)
        => SendAuthorizedRequestAsync(
            HttpMethod.Post,
            "api/v1/ai/translate",
            new TranslateRequest(html, targetLanguage),
            WinoAccountApiJsonContext.Default.TranslateRequest,
            WinoAccountApiJsonContext.Default.ApiEnvelopeAiTextResultDto,
            cancellationToken);

    public Task<ApiEnvelope<AiTextResultDto>> RewriteAsync(string html, string mode, CancellationToken cancellationToken = default)
        => SendAuthorizedRequestAsync(
            HttpMethod.Post,
            "api/v1/ai/rewrite",
            new RewriteRequest(html, mode),
            WinoAccountApiJsonContext.Default.RewriteRequest,
            WinoAccountApiJsonContext.Default.ApiEnvelopeAiTextResultDto,
            cancellationToken);

    public Task<ApiEnvelope<WinoStoreCollectionsIdTicketInfo>> CreateCollectionsIdTicketAsync(CancellationToken cancellationToken = default)
        => SendAuthorizedRequestAsync(
            HttpMethod.Post,
            "api/v1/store/collections-id-ticket",
            WinoAccountApiJsonContext.Default.ApiEnvelopeWinoStoreCollectionsIdTicketInfo,
            cancellationToken);

    public Task<ApiEnvelope<WinoStoreCollectionsIdTicketInfo>> CreatePurchaseIdTicketAsync(CancellationToken cancellationToken = default)
        => SendAuthorizedRequestAsync(
            HttpMethod.Post,
            "api/v1/store/purchase-id-ticket",
            WinoAccountApiJsonContext.Default.ApiEnvelopeWinoStoreCollectionsIdTicketInfo,
            cancellationToken);

    public Task<ApiEnvelope<JsonElement>> SyncStoreEntitlementsAsync(string? storeIdKey, string? purchaseIdKey, CancellationToken cancellationToken = default)
        => SendAuthorizedRequestAsync(
            HttpMethod.Post,
            "api/v1/store/entitlements/sync",
            new SyncStoreEntitlementsRequest(storeIdKey, purchaseIdKey),
            WinoAccountApiJsonContext.Default.SyncStoreEntitlementsRequest,
            WinoAccountApiJsonContext.Default.ApiEnvelopeJsonElement,
            cancellationToken);

    public async Task<string?> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(
            () => CreateAuthorizedRequestAsync(HttpMethod.Get, "api/v1/users/me/settings"),
            cancellationToken).ConfigureAwait(false);

        if (response == null)
        {
            throw new InvalidOperationException("MissingAccessToken");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return null;
        }

        await EnsureSuccessResponseAsync(response, cancellationToken).ConfigureAwait(false);

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveSettingsAsync(string settingsJson, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(
            () => CreateAuthorizedRequestAsync(
                HttpMethod.Put,
                "api/v1/users/me/settings",
                () => new StringContent(settingsJson, Encoding.UTF8, "application/json")),
            cancellationToken).ConfigureAwait(false);

        if (response == null)
        {
            throw new InvalidOperationException("MissingAccessToken");
        }

        await EnsureSuccessResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserMailboxSyncListDto> GetMailboxesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(
            () => CreateAuthorizedRequestAsync(HttpMethod.Get, "api/v1/users/me/mailboxes"),
            cancellationToken).ConfigureAwait(false);

        if (response == null)
        {
            throw new InvalidOperationException("MissingAccessToken");
        }

        await EnsureSuccessResponseAsync(response, cancellationToken).ConfigureAwait(false);

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var envelope = string.IsNullOrWhiteSpace(payload)
            ? null
            : JsonSerializer.Deserialize(payload, WinoAccountApiJsonContext.Default.ApiEnvelopeUserMailboxSyncListDto);

        if (envelope?.IsSuccess == true && envelope.Result != null)
        {
            return envelope.Result;
        }

        throw new InvalidOperationException(ExtractErrorMessage(payload) ?? envelope?.ErrorCode ?? "Mailbox synchronization request failed.");
    }

    public async Task ReplaceMailboxesAsync(ReplaceUserMailboxesRequestDto request, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(
            () => CreateAuthorizedRequestAsync(
                HttpMethod.Put,
                "api/v1/users/me/mailboxes",
                () => JsonContent.Create(request, WinoAccountApiJsonContext.Default.ReplaceUserMailboxesRequestDto)),
            cancellationToken).ConfigureAwait(false);

        if (response == null)
        {
            throw new InvalidOperationException("MissingAccessToken");
        }

        await EnsureSuccessResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WinoAccountApiResult<AuthResultDto>> SendAuthRequestAsync<TRequest>(string endpoint, TRequest request, JsonTypeInfo<TRequest> typeInfo, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                endpoint,
                request,
                typeInfo,
                cancellationToken).ConfigureAwait(false);

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var envelope = string.IsNullOrWhiteSpace(payload)
                ? null
                : JsonSerializer.Deserialize(payload, WinoAccountApiJsonContext.Default.ApiEnvelopeAuthResultDto);

            if (envelope?.IsSuccess == true && envelope.Result != null)
            {
                return WinoAccountApiResult<AuthResultDto>.Success(envelope.Result);
            }

            var errorCode = envelope?.ErrorCode ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim();
            var errorMessage = ExtractErrorMessage(payload) ?? response.ReasonPhrase;
            var errorDetails = ExtractDetails(payload);

            return WinoAccountApiResult<AuthResultDto>.Failure(errorCode, errorMessage, errorDetails);
        }
        catch (Exception ex)
        {
            return WinoAccountApiResult<AuthResultDto>.Failure(ex.GetType().Name, ex.Message);
        }
    }

    private static string? ExtractErrorMessage(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return TryGetErrorMessage(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? ExtractDetails(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("details", out var details))
            {
                return null;
            }

            return details.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryGetErrorMessage(JsonElement element)
    {
        if (TryGetStringProperty(element, "errorMessage", out var errorMessage))
        {
            return errorMessage;
        }

        if (TryGetStringProperty(element, "message", out var message))
        {
            return message;
        }

        if (TryGetStringProperty(element, "detail", out var detail))
        {
            return detail;
        }

        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("error", out var errorElement))
        {
            return TryGetErrorMessage(errorElement);
        }

        return null;
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string? value)
    {
        value = null;

        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static async Task EnsureSuccessResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException(
            ExtractErrorMessage(payload)
            ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim());
    }

    private Task<ApiEnvelope<TResponse>> SendAuthorizedRequestAsync<TResponse>(string endpoint, JsonTypeInfo<ApiEnvelope<TResponse>> typeInfo, CancellationToken cancellationToken)
        => SendAuthorizedRequestAsync(HttpMethod.Get, endpoint, typeInfo, cancellationToken);

    private async Task<ApiEnvelope<TResponse>> SendAnonymousRequestAsync<TRequest, TResponse>(HttpMethod method,
                                                                                               string endpoint,
                                                                                               TRequest requestBody,
                                                                                               JsonTypeInfo<TRequest> requestTypeInfo,
                                                                                               JsonTypeInfo<ApiEnvelope<TResponse>> responseTypeInfo,
                                                                                               CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, endpoint)
            {
                Content = JsonContent.Create(requestBody, requestTypeInfo)
            };

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var envelope = string.IsNullOrWhiteSpace(payload)
                ? null
                : JsonSerializer.Deserialize(payload, responseTypeInfo);

            return envelope ?? ApiEnvelope<TResponse>.Failure($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim());
        }
        catch (Exception ex)
        {
            return ApiEnvelope<TResponse>.Failure(ex.Message);
        }
    }

    private async Task<ApiEnvelope<TResponse>> SendAuthorizedRequestAsync<TResponse>(HttpMethod method, string endpoint, JsonTypeInfo<ApiEnvelope<TResponse>> typeInfo, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAuthorizedAsync(
                () => CreateAuthorizedRequestAsync(method, endpoint),
                cancellationToken).ConfigureAwait(false);

            if (response == null)
                return ApiEnvelope<TResponse>.Failure("MissingAccessToken");

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var envelope = string.IsNullOrWhiteSpace(payload)
                ? null
                : JsonSerializer.Deserialize(payload, typeInfo);

            return envelope ?? ApiEnvelope<TResponse>.Failure($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim());
        }
        catch (Exception ex)
        {
            return ApiEnvelope<TResponse>.Failure(ex.Message);
        }
    }

    private async Task<ApiEnvelope<TResponse>> SendAuthorizedRequestAsync<TRequest, TResponse>(HttpMethod method,
                                                                                                string endpoint,
                                                                                                TRequest requestBody,
                                                                                                JsonTypeInfo<TRequest> requestTypeInfo,
                                                                                                JsonTypeInfo<ApiEnvelope<TResponse>> responseTypeInfo,
                                                                                                CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAuthorizedAsync(
                () => CreateAuthorizedRequestAsync(
                    method,
                    endpoint,
                    () => JsonContent.Create(requestBody, requestTypeInfo)),
                cancellationToken).ConfigureAwait(false);

            if (response == null)
            {
                return ApiEnvelope<TResponse>.Failure("MissingAccessToken");
            }

            var payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var envelope = string.IsNullOrWhiteSpace(payload)
                ? null
                : JsonSerializer.Deserialize(payload, responseTypeInfo);

            return envelope ?? ApiEnvelope<TResponse>.Failure($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim());
        }
        catch (Exception ex)
        {
            return ApiEnvelope<TResponse>.Failure(ex.Message);
        }
    }

    private async Task<HttpRequestMessage?> CreateAuthorizedRequestAsync(HttpMethod method, string endpoint, Func<HttpContent>? contentFactory = null)
    {
        var accessToken = await GetAccessTokenAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
            return null;

        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = contentFactory?.Invoke();
        return request;
    }

    private async Task<HttpResponseMessage?> SendAuthorizedAsync(Func<Task<HttpRequestMessage?>> requestFactory, CancellationToken cancellationToken)
    {
        using var initialRequest = await requestFactory().ConfigureAwait(false);
        if (initialRequest == null)
        {
            return null;
        }

        var response = await _httpClient.SendAsync(initialRequest, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
        {
            return response;
        }

        if (!await TryRefreshAccessTokenAsync(cancellationToken).ConfigureAwait(false))
        {
            return response;
        }

        response.Dispose();

        using var retryRequest = await requestFactory().ConfigureAwait(false);
        if (retryRequest == null)
        {
            return null;
        }

        return await _httpClient.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> GetAccessTokenAsync()
    {
        var account = await _databaseService.Connection.Table<WinoAccount>().FirstOrDefaultAsync().ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(account?.AccessToken) ? null : account.AccessToken;
    }

    private async Task<bool> TryRefreshAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _tokenRefreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var account = await _databaseService.Connection.Table<WinoAccount>().FirstOrDefaultAsync().ConfigureAwait(false);
            if (account == null || string.IsNullOrWhiteSpace(account.RefreshToken))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(account.AccessToken) && account.AccessTokenExpiresAtUtc > DateTime.UtcNow)
            {
                return true;
            }

            var refreshResult = await RefreshAsync(account.RefreshToken, cancellationToken).ConfigureAwait(false);
            if (!refreshResult.IsSuccess || refreshResult.Result == null)
            {
                return false;
            }

            var refreshedAccount = MapAccount(refreshResult.Result, account.LastAuthenticatedUtc);

            await _databaseService.Connection.DeleteAllAsync<WinoAccount>().ConfigureAwait(false);
            await _databaseService.Connection.InsertOrReplaceAsync(refreshedAccount, typeof(WinoAccount)).ConfigureAwait(false);

            return true;
        }
        finally
        {
            _tokenRefreshLock.Release();
        }
    }

    private static WinoAccount MapAccount(AuthResultDto result, DateTime lastAuthenticatedUtc)
        => new()
        {
            Id = result.User.UserId,
            Email = result.User.Email,
            AccountStatus = result.User.AccountStatus,
            HasPassword = result.User.HasPassword,
            HasGoogleLogin = result.User.HasGoogleLogin,
            HasFacebookLogin = result.User.HasFacebookLogin,
            AccessToken = result.AccessToken,
            AccessTokenExpiresAtUtc = result.AccessTokenExpiresAtUtc.UtcDateTime,
            RefreshToken = result.RefreshToken,
            RefreshTokenExpiresAtUtc = result.RefreshTokenExpiresAtUtc.UtcDateTime,
            LastAuthenticatedUtc = lastAuthenticatedUtc == default ? DateTime.UtcNow : lastAuthenticatedUtc
        };

    private static bool ValidateCertificate(HttpRequestMessage requestMessage, X509Certificate2? certificate, X509Chain? chain, System.Net.Security.SslPolicyErrors sslPolicyErrors)
    {
        if (requestMessage.RequestUri?.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return sslPolicyErrors == System.Net.Security.SslPolicyErrors.None;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        _tokenRefreshLock.Dispose();
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RegisterRequest))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(RefreshRequest))]
[JsonSerializable(typeof(LogoutRequest))]
[JsonSerializable(typeof(ResendEmailConfirmationRequest))]
[JsonSerializable(typeof(ForgotPasswordRequest))]
[JsonSerializable(typeof(SummarizeRequest))]
[JsonSerializable(typeof(TranslateRequest))]
[JsonSerializable(typeof(RewriteRequest))]
[JsonSerializable(typeof(SyncStoreEntitlementsRequest))]
[JsonSerializable(typeof(ApiEnvelope<AuthResultDto>))]
[JsonSerializable(typeof(ApiEnvelope<EmailConfirmationResendResultDto>))]
[JsonSerializable(typeof(ApiEnvelope<AuthUserDto>))]
[JsonSerializable(typeof(ApiEnvelope<AiStatusResultDto>))]
[JsonSerializable(typeof(ApiEnvelope<AiTextResultDto>))]
[JsonSerializable(typeof(ApiEnvelope<WinoStoreCollectionsIdTicketInfo>))]
[JsonSerializable(typeof(ApiEnvelope<UserMailboxSyncListDto>))]
[JsonSerializable(typeof(ApiEnvelope<JsonElement>))]
[JsonSerializable(typeof(ReplaceUserMailboxesRequestDto))]
[JsonSerializable(typeof(List<UserMailboxSyncItemDto>))]
internal sealed partial class WinoAccountApiJsonContext : JsonSerializerContext;

internal sealed record SyncStoreEntitlementsRequest(string? StoreIdKey, string? PurchaseIdKey);
