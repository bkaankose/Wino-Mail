#nullable enable
using System;
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
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.Api.Contracts.Billing;
using Wino.Mail.Api.Contracts.Common;

namespace Wino.Services;

public sealed class WinoAccountApiClient : IWinoAccountApiClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IDatabaseService _databaseService;
    private readonly bool _ownsHttpClient;

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
            BaseAddress = new Uri("https://api.winomail.app/")
        };
        _ownsHttpClient = true;
    }

    public Task<WinoAccountApiResult<AuthResultDto>> RegisterAsync(string email, string password, CancellationToken cancellationToken = default)
        => SendAuthRequestAsync("api/v1/auth/register", new RegisterRequest(email, password), WinoAccountApiJsonContext.Default.RegisterRequest, cancellationToken);

    public Task<WinoAccountApiResult<AuthResultDto>> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
        => SendAuthRequestAsync("api/v1/auth/login", new LoginRequest(email, password), WinoAccountApiJsonContext.Default.LoginRequest, cancellationToken);

    public Task<WinoAccountApiResult<AuthResultDto>> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
        => SendAuthRequestAsync("api/v1/auth/refresh", new RefreshRequest(refreshToken), WinoAccountApiJsonContext.Default.RefreshRequest, cancellationToken);

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

    public Task<ApiEnvelope<CheckoutSessionResultDto>> CreateCheckoutSessionAsync(WinoAddOnProductType productId, CancellationToken cancellationToken = default)
    {
        var endpoint = productId switch
        {
            WinoAddOnProductType.AI_PACK => "api/v1/billing/ai-pack/checkout-session",
            WinoAddOnProductType.UNLIMITED_ACCOUNTS => "api/v1/billing/unlimited-accounts/checkout-session",
            _ => string.Empty
        };

        return string.IsNullOrWhiteSpace(endpoint)
            ? Task.FromResult(ApiEnvelope<CheckoutSessionResultDto>.Failure("UnknownProduct"))
            : SendAuthorizedRequestAsync(HttpMethod.Post, endpoint, WinoAccountApiJsonContext.Default.ApiEnvelopeCheckoutSessionResultDto, cancellationToken);
    }

    public Task<ApiEnvelope<CustomerPortalResultDto>> CreateCustomerPortalSessionAsync(CancellationToken cancellationToken = default)
        => SendAuthorizedRequestAsync(
            HttpMethod.Post,
            "api/v1/billing/ai-pack/customer-portal-session",
            WinoAccountApiJsonContext.Default.ApiEnvelopeCustomerPortalResultDto,
            cancellationToken);

    public async Task<string?> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = await CreateAuthorizedRequestAsync(HttpMethod.Get, "api/v1/users/me/settings").ConfigureAwait(false);
            if (request == null)
                return null;

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                return null;

            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> SaveSettingsAsync(string settingsJson, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = await CreateAuthorizedRequestAsync(HttpMethod.Put, "api/v1/users/me/settings").ConfigureAwait(false);
            if (request == null)
                return false;

            request.Content = new StringContent(settingsJson, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
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

            return WinoAccountApiResult<AuthResultDto>.Failure(errorCode, errorMessage);
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

    private Task<ApiEnvelope<TResponse>> SendAuthorizedRequestAsync<TResponse>(string endpoint, JsonTypeInfo<ApiEnvelope<TResponse>> typeInfo, CancellationToken cancellationToken)
        => SendAuthorizedRequestAsync(HttpMethod.Get, endpoint, typeInfo, cancellationToken);

    private async Task<ApiEnvelope<TResponse>> SendAuthorizedRequestAsync<TResponse>(HttpMethod method, string endpoint, JsonTypeInfo<ApiEnvelope<TResponse>> typeInfo, CancellationToken cancellationToken)
    {
        try
        {
            using var request = await CreateAuthorizedRequestAsync(method, endpoint).ConfigureAwait(false);
            if (request == null)
                return ApiEnvelope<TResponse>.Failure("MissingAccessToken");

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

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

    private async Task<HttpRequestMessage?> CreateAuthorizedRequestAsync(HttpMethod method, string endpoint)
    {
        var accessToken = await GetAccessTokenAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
            return null;

        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private async Task<string?> GetAccessTokenAsync()
    {
        var account = await _databaseService.Connection.Table<WinoAccount>().FirstOrDefaultAsync().ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(account?.AccessToken) ? null : account.AccessToken;
    }

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
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RegisterRequest))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(RefreshRequest))]
[JsonSerializable(typeof(LogoutRequest))]
[JsonSerializable(typeof(ApiEnvelope<AuthResultDto>))]
[JsonSerializable(typeof(ApiEnvelope<AuthUserDto>))]
[JsonSerializable(typeof(ApiEnvelope<AiStatusResultDto>))]
[JsonSerializable(typeof(ApiEnvelope<CheckoutSessionResultDto>))]
[JsonSerializable(typeof(ApiEnvelope<CustomerPortalResultDto>))]
[JsonSerializable(typeof(ApiEnvelope<JsonElement>))]
internal sealed partial class WinoAccountApiJsonContext : JsonSerializerContext;
