#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.Cryptography;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.Api.Contracts.Billing;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Api.Contracts.Users;
using Wino.Mail.Contracts.Intelligence;

namespace Wino.Services;

public sealed class WinoAccountApiClient : IWinoAccountApiClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IDatabaseService _databaseService;
    private readonly IContentEnvelopeEncryptor _contentEnvelopeEncryptor;
    private readonly ITranslationService? _translationService;
    private readonly IWinoAccountSessionService _sessions;
    private readonly bool _ownsHttpClient;
    private readonly int _maximumEncryptedAttempts;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);

    // private const string ApiUrl = "https://localhost:7204/";
    private const string ApiUrl = "https://api.winomail.app/";

    public WinoAccountApiClient(
        IDatabaseService databaseService,
        HttpClient? httpClient = null,
        IContentEnvelopeEncryptor? contentEnvelopeEncryptor = null,
        ITranslationService? translationService = null,
        int maximumEncryptedAttempts = 5,
        IWinoAccountSessionService? sessionService = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEncryptedAttempts, 1);

        _databaseService = databaseService;
        _sessions = sessionService ?? WinoAccountSessionService.For(databaseService);
        _contentEnvelopeEncryptor = contentEnvelopeEncryptor ??
            new PemContentEnvelopeEncryptor(EmbeddedIntelligencePublicKeyProvider.Load());
        _translationService = translationService;
        _maximumEncryptedAttempts = maximumEncryptedAttempts;

        if (httpClient != null)
        {
            _httpClient = httpClient;
            ConfigureHttpVersion(_httpClient);
            return;
        }

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = ValidateCertificate,
            AutomaticDecompression = DecompressionMethods.Brotli | DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(ApiUrl)
        };
        ConfigureHttpVersion(_httpClient);

        _ownsHttpClient = true;
    }

    private static void ConfigureHttpVersion(HttpClient client)
    {
        client.Timeout = RequestTimeout;

        // TODO: Azure Support HTTPS 2.0
        //client.DefaultRequestVersion = HttpVersion.Version20;
        //client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
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

    public async Task<IntelligenceConsentDto> GetIntelligenceConsentAsync(CancellationToken cancellationToken = default)
    {
        var envelope = await SendAuthorizedRequestAsync("api/v1/ai/consent", WinoAccountApiJsonContext.Default.ApiEnvelopeIntelligenceConsentDto, cancellationToken).ConfigureAwait(false);
        return envelope.IsSuccess && envelope.Result is not null ? envelope.Result : throw new InvalidOperationException(envelope.ErrorCode ?? "Intelligence consent could not be loaded.");
    }

    public async Task<IntelligenceConsentDto> AcceptIntelligenceConsentAsync(string policyVersion, string source, CancellationToken cancellationToken = default)
    {
        var request = new UpdateIntelligenceConsentRequest(policyVersion, source);
        var envelope = await SendAuthorizedRequestAsync(HttpMethod.Put, "api/v1/ai/consent", request, WinoAccountApiJsonContext.Default.UpdateIntelligenceConsentRequest, WinoAccountApiJsonContext.Default.ApiEnvelopeIntelligenceConsentDto, cancellationToken).ConfigureAwait(false);
        return envelope.IsSuccess && envelope.Result is not null ? envelope.Result : throw new InvalidOperationException(envelope.ErrorCode ?? "Intelligence consent could not be saved.");
    }

    public async Task<IntelligenceConsentDto> RevokeIntelligenceConsentAsync(string source, CancellationToken cancellationToken = default)
    {
        var request = new RevokeIntelligenceConsentRequest(source);
        var envelope = await SendAuthorizedRequestAsync(HttpMethod.Delete, "api/v1/ai/consent", request, WinoAccountApiJsonContext.Default.RevokeIntelligenceConsentRequest, WinoAccountApiJsonContext.Default.ApiEnvelopeIntelligenceConsentDto, cancellationToken).ConfigureAwait(false);
        return envelope.IsSuccess && envelope.Result is not null ? envelope.Result : throw new InvalidOperationException(envelope.ErrorCode ?? "Intelligence consent could not be revoked.");
    }

    public async Task<ApiEnvelope<AiSummaryResultDto>> SummarizeAsync(IReadOnlyList<MailContentSegment> segments, string targetLanguage, CancellationToken cancellationToken = default)
    {
        return await SendAuthorizedRequestAsync(
            HttpMethod.Post,
            "api/v2/ai/summarize",
            new SummarizeRequest(segments, targetLanguage),
            WinoAccountApiJsonContext.Default.SummarizeRequest,
            WinoAccountApiJsonContext.Default.ApiEnvelopeAiSummaryResultDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiEnvelope<AiTranslationResultDto>> TranslateAsync(IReadOnlyList<MailContentSegment> segments, string? sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
    {
        return await SendAuthorizedRequestAsync(
            HttpMethod.Post,
            "api/v2/ai/translate",
            new TranslateRequest(segments, sourceLanguage, targetLanguage),
            WinoAccountApiJsonContext.Default.TranslateRequest,
            WinoAccountApiJsonContext.Default.ApiEnvelopeAiTranslationResultDto,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApiEnvelope<AiTextResultDto>> RewriteAsync(string html, string mode, string language, CancellationToken cancellationToken = default)
    {
        return await SendAuthorizedRequestAsync(
            HttpMethod.Post,
            "api/v1/ai/rewrite",
            new LocalizedRewriteRequest(html, mode, language),
            WinoAccountApiJsonContext.Default.LocalizedRewriteRequest,
            WinoAccountApiJsonContext.Default.ApiEnvelopeAiTextResultDto,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<ApiEnvelope<CheckoutSessionResultDto>> CreateCheckoutSessionAsync(string productCode, CancellationToken cancellationToken = default)
        => SendAuthorizedRequestAsync(
            HttpMethod.Post,
            "api/v1/billing/checkout-session",
            new CreateCheckoutSessionRequest(productCode),
            WinoAccountApiJsonContext.Default.CreateCheckoutSessionRequest,
            WinoAccountApiJsonContext.Default.ApiEnvelopeCheckoutSessionResultDto,
            cancellationToken);

    public Task<ApiEnvelope<BillingStatusResultDto>> GetBillingStatusAsync(CancellationToken cancellationToken = default)
        => SendAuthorizedRequestAsync(
            "api/v1/billing/status",
            WinoAccountApiJsonContext.Default.ApiEnvelopeBillingStatusResultDto,
            cancellationToken);

    public Task<ApiEnvelope<AiUsageStatusDto>> GetAiUsageAsync(CancellationToken cancellationToken = default)
        => SendAuthorizedRequestAsync(
            "api/v1/ai/usage",
            WinoAccountApiJsonContext.Default.ApiEnvelopeAiUsageStatusDto,
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

    private const string MailIntelligenceRoot = "api/v2/ai/intelligence";

    private static string MailIntelligenceJobsRoute(Guid mailboxId)
        => $"{MailIntelligenceRoot}/mailboxes/{mailboxId:D}/jobs";

    private static string MailIntelligenceJobRoute(Guid mailboxId, Guid jobId)
        => $"{MailIntelligenceJobsRoute(mailboxId)}/{jobId:D}";

    /// <summary>
    /// Streams one encrypted SQLite upload. The job id and checksum make a retry
    /// idempotent, so a dropped connection never produces a duplicate job.
    /// </summary>
    public async Task<MailIntelligenceJobAcceptedDto> SubmitMailIntelligenceJobAsync(
        Guid mailboxId,
        Guid jobId,
        string checksum,
        byte[] upload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        var endpoint = $"{MailIntelligenceJobsRoute(mailboxId)}?jobId={jobId:D}&checksum={checksum}";

        for (var attempt = 0; attempt < _maximumEncryptedAttempts; attempt++)
        {
            try
            {
                using var response = await SendAuthorizedAsync(
                    () => CreateAuthorizedRequestAsync(
                        HttpMethod.Post,
                        endpoint,
                        () =>
                        {
                            var content = new ByteArrayContent(upload);
                            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                            return content;
                        }),
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("MissingAccessToken");

                if (attempt < _maximumEncryptedAttempts - 1 &&
                    response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or
                        HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var envelope = await JsonSerializer.DeserializeAsync(
                    stream,
                    WinoAccountApiJsonContext.Default.ApiEnvelopeMailIntelligenceJobAcceptedDto,
                    cancellationToken).ConfigureAwait(false);
                return RequireResult(
                    envelope ?? ApiEnvelope<MailIntelligenceJobAcceptedDto>.Failure($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim()),
                    "Submitting the intelligence job failed.");
            }
            catch (HttpRequestException) when (attempt < _maximumEncryptedAttempts - 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("Submitting the intelligence job failed. Retry limit reached.");
    }

    public async Task<MailIntelligenceJobListDto> GetMailIntelligenceJobsAsync(
        string? resultKeyId = null, CancellationToken cancellationToken = default)
        => RequireResult(await SendAuthorizedRequestAsync(
            HttpMethod.Get,
            string.IsNullOrEmpty(resultKeyId)
                ? $"{MailIntelligenceRoot}/jobs"
                : $"{MailIntelligenceRoot}/jobs?resultKeyId={Uri.EscapeDataString(resultKeyId)}",
            WinoAccountApiJsonContext.Default.ApiEnvelopeMailIntelligenceJobListDto,
            cancellationToken).ConfigureAwait(false), "Listing intelligence jobs failed.");

    /// <summary>The server transport key uploads are encrypted to. Public material; bearer only.</summary>
    public async Task<IntelligenceTransportKeyDto> GetIntelligenceTransportKeyAsync(CancellationToken cancellationToken = default)
        => RequireResult(await SendAuthorizedRequestAsync(
            HttpMethod.Get,
            $"{MailIntelligenceRoot}/transport-key",
            WinoAccountApiJsonContext.Default.ApiEnvelopeIntelligenceTransportKeyDto,
            cancellationToken).ConfigureAwait(false), "Loading the intelligence transport key failed.");

    /// <summary>Returns null when the job is gone, which happens once both stages are acknowledged.</summary>
    public async Task<MailIntelligenceJobDto?> GetMailIntelligenceJobAsync(
        Guid mailboxId, Guid jobId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(
            () => CreateAuthorizedRequestAsync(HttpMethod.Get, MailIntelligenceJobRoute(mailboxId, jobId)),
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("MissingAccessToken");

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var envelope = await JsonSerializer.DeserializeAsync(
            stream,
            WinoAccountApiJsonContext.Default.ApiEnvelopeMailIntelligenceJobDto,
            cancellationToken).ConfigureAwait(false);
        return RequireResult(
            envelope ?? ApiEnvelope<MailIntelligenceJobDto>.Failure($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim()),
            "Reading the intelligence job failed.");
    }

    /// <summary>
    /// Downloads one result page as its JSON bytes. A job bound to a device result key gets an
    /// <see cref="EncryptedResultPageDto"/>; a job submitted before results were encrypted gets
    /// the stage page itself.
    /// </summary>
    public async Task<byte[]> GetMailIntelligenceResultPageAsync(
        Guid mailboxId,
        Guid jobId,
        string stage,
        int page,
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(
            () => CreateAuthorizedRequestAsync(
                HttpMethod.Get,
                $"{MailIntelligenceJobRoute(mailboxId, jobId)}/results/{stage}?page={page}"),
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("MissingAccessToken");
        await EnsureSuccessResponseAsync(response, cancellationToken).ConfigureAwait(false);

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (content.Length == 0)
        {
            throw new InvalidOperationException($"The {stage} result page was empty.");
        }

        return content;
    }

    public async Task<MailIntelligenceStageAckResultDto> AcknowledgeMailIntelligenceStageAsync(
        Guid mailboxId, Guid jobId, string stage, string digest, CancellationToken cancellationToken = default)
        => RequireResult(await SendAuthorizedRequestAsync(
            HttpMethod.Post,
            $"{MailIntelligenceJobRoute(mailboxId, jobId)}/results/{stage}:ack",
            new MailIntelligenceStageAckRequest(digest),
            WinoAccountApiJsonContext.Default.MailIntelligenceStageAckRequest,
            WinoAccountApiJsonContext.Default.ApiEnvelopeMailIntelligenceStageAckResultDto,
            cancellationToken).ConfigureAwait(false), "Acknowledging the intelligence stage failed.");

    public async Task CancelMailIntelligenceJobAsync(Guid mailboxId, Guid jobId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(
            () => CreateAuthorizedRequestAsync(HttpMethod.Delete, MailIntelligenceJobRoute(mailboxId, jobId)),
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("MissingAccessToken");
        await EnsureSuccessResponseAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Processes one mail synchronously. The response carries the Classification artifact always and
    /// the Enrichment artifact whenever Classification selected the message for the briefing.
    /// </summary>
    public async Task<AnalyzeMailResponseDto> AnalyzeMailAsync(
        Guid mailboxId,
        byte[] encryptedEnvelope,
        string language,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(encryptedEnvelope);
        var endpoint = $"{MailIntelligenceRoot}/mailboxes/{mailboxId:D}/messages:analyze?language={Uri.EscapeDataString(language)}";
        using var response = await SendAuthorizedAsync(
            () => CreateAuthorizedRequestAsync(
                HttpMethod.Post,
                endpoint,
                () =>
                {
                    var content = new ByteArrayContent(encryptedEnvelope);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    return content;
                }),
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("MissingAccessToken");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var envelope = await JsonSerializer.DeserializeAsync(
            stream,
            WinoAccountApiJsonContext.Default.ApiEnvelopeAnalyzeMailResponseDto,
            cancellationToken).ConfigureAwait(false);
        return RequireResult(
            envelope ?? ApiEnvelope<AnalyzeMailResponseDto>.Failure($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim()),
            "Analyzing the message failed.");
    }

    private async Task<T> SendEncryptedIntelligenceAsync<T>(
        string endpoint,
        byte[] encryptedEnvelope,
        JsonTypeInfo<ApiEnvelope<T>> responseType,
        string failureMessage,
        CancellationToken cancellationToken) where T : class
    {
        for (var attempt = 0; attempt < _maximumEncryptedAttempts; attempt++)
        {
            try
            {
                using var response = await SendAuthorizedAsync(
                    () => CreateAuthorizedRequestAsync(
                        HttpMethod.Post,
                        endpoint,
                        () =>
                        {
                            var content = new ByteArrayContent(encryptedEnvelope);
                            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                            return content;
                        }),
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("MissingAccessToken");
                if (attempt < _maximumEncryptedAttempts - 1 && response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.Conflict or
                    HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var responseEnvelope = await JsonSerializer.DeserializeAsync(
                    stream,
                    responseType,
                    cancellationToken).ConfigureAwait(false);
                return RequireResult(
                    responseEnvelope ?? ApiEnvelope<T>.Failure($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim()),
                    failureMessage);
            }
            catch (HttpRequestException) when (attempt < _maximumEncryptedAttempts - 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException($"{failureMessage} Retry limit reached.");
    }

    private async Task<T> SendEncryptedIntelligenceOnceAsync<T>(
        string endpoint,
        byte[] encryptedEnvelope,
        JsonTypeInfo<ApiEnvelope<T>> responseType,
        string failureMessage,
        CancellationToken cancellationToken) where T : class
    {
        using var request = await CreateAuthorizedRequestAsync(
                HttpMethod.Post,
                endpoint,
                () =>
                {
                    var content = new ByteArrayContent(encryptedEnvelope);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    return content;
                }).ConfigureAwait(false)
            ?? throw new InvalidOperationException("MissingAccessToken");
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var responseEnvelope = await JsonSerializer.DeserializeAsync(stream, responseType, cancellationToken).ConfigureAwait(false);
        return RequireResult(
            responseEnvelope ?? ApiEnvelope<T>.Failure($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim()),
            failureMessage);
    }

    private static T RequireResult<T>(ApiEnvelope<T> envelope, string fallback) where T : class
        => envelope.IsSuccess && envelope.Result is not null
            ? envelope.Result
            : throw IntelligenceApiFailure(envelope.ErrorCode, fallback);

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

    private static InvalidOperationException SemanticApiFailure(string? errorCode, string fallbackMessage)
        => new(errorCode switch
        {
            ApiErrorCodes.IntelligenceUploadTooLarge => Translator.SemanticIndex_StorageLimitExceeded,
            _ => errorCode ?? fallbackMessage,
        });

    private static InvalidOperationException IntelligenceApiFailure(string? errorCode, string fallbackMessage)
        => new(errorCode switch
        {
            ApiErrorCodes.AiQuotaExceeded => Translator.Intelligence_QuotaExceeded,
            _ => errorCode ?? fallbackMessage,
        });

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

    private static string? ExtractErrorCode(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            return TryGetStringProperty(document.RootElement, "errorCode", out var errorCode)
                ? errorCode
                : null;
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
            ExtractErrorCode(payload)
            ?? ExtractErrorMessage(payload)
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
        var session = await _sessions.CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (session is null) return null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.CancellationToken);
        cancellationToken = linked.Token;
        using var initialRequest = await requestFactory().ConfigureAwait(false);
        if (initialRequest == null)
        {
            return null;
        }

        var response = await _httpClient.SendAsync(initialRequest, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            response.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
        {
            return response;
        }

        if (!await TryRefreshAccessTokenAsync(session, initialRequest.Headers.Authorization?.Parameter, cancellationToken).ConfigureAwait(false))
        {
            return response;
        }

        response.Dispose();

        using var retryRequest = await requestFactory().ConfigureAwait(false);
        if (retryRequest == null)
        {
            return null;
        }

        var retryResponse = await _httpClient.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            retryResponse.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }

        return retryResponse;
    }

    private async Task<string?> GetAccessTokenAsync()
    {
        var account = await _databaseService.Connection.Table<WinoAccount>().FirstOrDefaultAsync().ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(account?.AccessToken) ? null : account.AccessToken;
    }

    private string GetApplicationLanguage()
    {
        var language = _translationService?.CurrentLanguageModel?.Code;
        if (!string.IsNullOrWhiteSpace(language))
            return language;

        return string.IsNullOrWhiteSpace(CultureInfo.CurrentUICulture.Name)
            ? "en-US"
            : CultureInfo.CurrentUICulture.Name;
    }

    private async Task<bool> TryRefreshAccessTokenAsync(WinoAccountSession session, string? rejectedAccessToken, CancellationToken cancellationToken)
    {
        var refreshed = await _sessions.RefreshCredentialsAsync(session, rejectedAccessToken, async (account, token) =>
        {
            var result = await RefreshAsync(account.RefreshToken, token).ConfigureAwait(false);
            return result.IsSuccess && result.Result is not null
                ? MapAccount(result.Result, account.LastAuthenticatedUtc) : null;
        }, cancellationToken).ConfigureAwait(false);

        return refreshed is not null;
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
            IsUnlimitedAccountsEnabled = result.User.IsUnlimitedAccountsEnabled,
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

    private sealed class DeltaFrameContent : HttpContent
    {
        private readonly Func<Stream, CancellationToken, Task> _writeAsync;

        public DeltaFrameContent(Func<Stream, CancellationToken, Task> writeAsync)
        {
            _writeAsync = writeAsync;
            Headers.ContentType = new MediaTypeHeaderValue("application/x-wino-encrypted-frames");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => _writeAsync(stream, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            => _writeAsync(stream, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
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
[JsonSerializable(typeof(ResendEmailConfirmationRequest))]
[JsonSerializable(typeof(ForgotPasswordRequest))]
[JsonSerializable(typeof(SummarizeRequest))]
[JsonSerializable(typeof(TranslateRequest))]
[JsonSerializable(typeof(RewriteRequest))]
[JsonSerializable(typeof(LocalizedRewriteRequest))]
[JsonSerializable(typeof(CreateCheckoutSessionRequest))]
[JsonSerializable(typeof(ApiEnvelope<AuthResultDto>))]
[JsonSerializable(typeof(ApiEnvelope<EmailConfirmationResendResultDto>))]
[JsonSerializable(typeof(ApiEnvelope<AuthUserDto>))]
[JsonSerializable(typeof(ApiEnvelope<AiTextResultDto>))]
[JsonSerializable(typeof(ApiEnvelope<AiSummaryResultDto>))]
[JsonSerializable(typeof(ApiEnvelope<AiTranslationResultDto>))]
[JsonSerializable(typeof(MailTranslationResult))]
[JsonSerializable(typeof(ApiEnvelope<CheckoutSessionResultDto>))]
[JsonSerializable(typeof(ApiEnvelope<BillingStatusResultDto>))]
[JsonSerializable(typeof(ApiEnvelope<AiUsageStatusDto>))]
[JsonSerializable(typeof(ApiEnvelope<UserMailboxSyncListDto>))]
[JsonSerializable(typeof(ApiEnvelope<JsonElement>))]
[JsonSerializable(typeof(ReplaceUserMailboxesRequestDto))]
[JsonSerializable(typeof(List<UserMailboxSyncItemDto>))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(ApiEnvelope<MailIntelligenceJobAcceptedDto>))]
[JsonSerializable(typeof(ApiEnvelope<MailIntelligenceJobListDto>))]
[JsonSerializable(typeof(ApiEnvelope<IntelligenceTransportKeyDto>))]
[JsonSerializable(typeof(EncryptedResultPageDto))]
[JsonSerializable(typeof(ApiEnvelope<MailIntelligenceJobDto>))]
[JsonSerializable(typeof(ApiEnvelope<MailIntelligenceStageAckResultDto>))]
[JsonSerializable(typeof(ApiEnvelope<AnalyzeMailResponseDto>))]
[JsonSerializable(typeof(MailIntelligenceStageAckRequest))]
[JsonSerializable(typeof(MailIntelligenceJobDto))]
[JsonSerializable(typeof(MailIntelligenceStageStatusDto))]
[JsonSerializable(typeof(ClassificationResultPageDto))]
[JsonSerializable(typeof(SummaryResultPageDto))]
[JsonSerializable(typeof(MailClassificationArtifactDto))]
[JsonSerializable(typeof(MailSummaryArtifactDto))]
[JsonSerializable(typeof(MailClassificationSignalsDto))]
[JsonSerializable(typeof(MailArtifactIdentityDto))]
[JsonSerializable(typeof(MailIntelligenceFailureDto))]
[JsonSerializable(typeof(AnalyzeMailResponseDto))]
[JsonSerializable(typeof(MailIntelligenceUploadEnvelopeDto))]
[JsonSerializable(typeof(MailSmartLabel))]
[JsonSerializable(typeof(MailPriority))]
[JsonSerializable(typeof(IntelligenceConsentDto))]
[JsonSerializable(typeof(UpdateIntelligenceConsentRequest))]
[JsonSerializable(typeof(RevokeIntelligenceConsentRequest))]
[JsonSerializable(typeof(ApiEnvelope<IntelligenceConsentDto>))]
internal sealed partial class WinoAccountApiJsonContext : JsonSerializerContext;

