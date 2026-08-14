#nullable enable
using System.Buffers.Binary;
using System;
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
using Wino.Mail.Contracts.SemanticIndex;

namespace Wino.Services;

public sealed class WinoAccountApiClient : IWinoAccountApiClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IDatabaseService _databaseService;
    private readonly IContentEnvelopeEncryptor _contentEnvelopeEncryptor;
    private readonly ITranslationService? _translationService;
    private readonly SemaphoreSlim _tokenRefreshLock = new(1, 1);
    private readonly bool _ownsHttpClient;

    private const string ApiUrl = "https://localhost:7204/";
    // private const string ApiUrl = "https://api.winomail.app/";

    public WinoAccountApiClient(
        IDatabaseService databaseService,
        HttpClient? httpClient = null,
        IContentEnvelopeEncryptor? contentEnvelopeEncryptor = null,
        ITranslationService? translationService = null)
    {
        _databaseService = databaseService;
        _contentEnvelopeEncryptor = contentEnvelopeEncryptor ??
            new PemContentEnvelopeEncryptor(EmbeddedIntelligencePublicKeyProvider.Load());
        _translationService = translationService;

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

    public async Task<TransportConsentDto> GetTransportConsentAsync(CancellationToken cancellationToken = default)
    {
        var envelope = await SendAuthorizedRequestAsync("api/v1/ai/consents/transport", WinoAccountApiJsonContext.Default.ApiEnvelopeTransportConsentDto, cancellationToken).ConfigureAwait(false);
        return envelope.IsSuccess && envelope.Result is not null ? envelope.Result : throw new InvalidOperationException(envelope.ErrorCode ?? "Transport consent could not be loaded.");
    }

    public async Task<TransportConsentDto> AcceptTransportConsentAsync(string policyVersion, string source, CancellationToken cancellationToken = default)
    {
        var request = new UpdateTransportConsentRequest(policyVersion, source);
        var envelope = await SendAuthorizedRequestAsync(HttpMethod.Put, "api/v1/ai/consents/transport", request, WinoAccountApiJsonContext.Default.UpdateTransportConsentRequest, WinoAccountApiJsonContext.Default.ApiEnvelopeTransportConsentDto, cancellationToken).ConfigureAwait(false);
        return envelope.IsSuccess && envelope.Result is not null ? envelope.Result : throw new InvalidOperationException(envelope.ErrorCode ?? "Transport consent could not be saved.");
    }

    public async Task<TransportConsentDto> RevokeTransportConsentAsync(string source, CancellationToken cancellationToken = default)
    {
        var request = new RevokeProcessConsentRequest(source);
        var envelope = await SendAuthorizedRequestAsync(HttpMethod.Delete, "api/v1/ai/consents/transport", request, WinoAccountApiJsonContext.Default.RevokeProcessConsentRequest, WinoAccountApiJsonContext.Default.ApiEnvelopeTransportConsentDto, cancellationToken).ConfigureAwait(false);
        return envelope.IsSuccess && envelope.Result is not null ? envelope.Result : throw new InvalidOperationException(envelope.ErrorCode ?? "Transport consent could not be revoked.");
    }

    public async Task<ProcessConsentListDto> GetProcessConsentsAsync(CancellationToken cancellationToken = default)
    {
        var envelope = await SendAuthorizedRequestAsync("api/v1/ai/consents/process", WinoAccountApiJsonContext.Default.ApiEnvelopeProcessConsentListDto, cancellationToken).ConfigureAwait(false);
        return envelope.IsSuccess && envelope.Result is not null ? envelope.Result : throw new InvalidOperationException(envelope.ErrorCode ?? "Process consent could not be loaded.");
    }

    public async Task<MailboxProcessConsentDto> AcceptProcessConsentAsync(Guid mailboxId, string policyVersion, string source, CancellationToken cancellationToken = default)
    {
        var request = new UpdateProcessConsentRequest(policyVersion, source);
        var envelope = await SendAuthorizedRequestAsync(HttpMethod.Put, $"api/v1/ai/consents/process/{mailboxId:D}", request, WinoAccountApiJsonContext.Default.UpdateProcessConsentRequest, WinoAccountApiJsonContext.Default.ApiEnvelopeMailboxProcessConsentDto, cancellationToken).ConfigureAwait(false);
        return envelope.IsSuccess && envelope.Result is not null ? envelope.Result : throw new InvalidOperationException(envelope.ErrorCode ?? "Process consent could not be saved.");
    }

    public async Task<MailboxProcessConsentDto> RevokeProcessConsentAsync(Guid mailboxId, string source, CancellationToken cancellationToken = default)
    {
        var request = new RevokeProcessConsentRequest(source);
        var envelope = await SendAuthorizedRequestAsync(HttpMethod.Delete, $"api/v1/ai/consents/process/{mailboxId:D}", request, WinoAccountApiJsonContext.Default.RevokeProcessConsentRequest, WinoAccountApiJsonContext.Default.ApiEnvelopeMailboxProcessConsentDto, cancellationToken).ConfigureAwait(false);
        return envelope.IsSuccess && envelope.Result is not null ? envelope.Result : throw new InvalidOperationException(envelope.ErrorCode ?? "Process consent could not be revoked.");
    }

    public async Task<BatchProcessConsentResult> UpdateProcessConsentsAsync(BatchProcessConsentRequest request, CancellationToken cancellationToken = default)
    {
        var envelope = await SendAuthorizedRequestAsync(HttpMethod.Post, "api/v1/ai/consents/process:batch", request, WinoAccountApiJsonContext.Default.BatchProcessConsentRequest, WinoAccountApiJsonContext.Default.ApiEnvelopeBatchProcessConsentResult, cancellationToken).ConfigureAwait(false);
        return envelope.IsSuccess && envelope.Result is not null ? envelope.Result : throw new InvalidOperationException(envelope.ErrorCode ?? "Process consents could not be updated.");
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

    public async Task<IReadOnlyList<SemanticMailboxDto>> GetSemanticMailboxesAsync(CancellationToken cancellationToken = default)
    {
        var envelope = await SendAuthorizedRequestAsync(
            "api/v1/ai/semantic-index/mailboxes",
            WinoAccountApiJsonContext.Default.ApiEnvelopeListSemanticMailboxDto,
            cancellationToken).ConfigureAwait(false);
        return envelope.IsSuccess && envelope.Result != null
            ? envelope.Result
            : throw new InvalidOperationException(envelope.ErrorCode ?? "Semantic mailbox discovery failed.");
    }

    public async Task<SemanticMailboxDto> EnsureSemanticMailboxAsync(
        string address,
        int providerType,
        CancellationToken cancellationToken = default)
    {
        var envelope = await SendAuthorizedRequestAsync(
            HttpMethod.Put,
            "api/v1/ai/semantic-index/mailboxes",
            new EnsureSemanticMailboxRequest(address, providerType),
            WinoAccountApiJsonContext.Default.EnsureSemanticMailboxRequest,
            WinoAccountApiJsonContext.Default.ApiEnvelopeSemanticMailboxDto,
            cancellationToken).ConfigureAwait(false);
        return envelope.IsSuccess && envelope.Result != null
            ? envelope.Result
            : throw SemanticApiFailure(envelope.ErrorCode, "Semantic mailbox creation failed.");
    }

    public async Task<IntelligenceManifestDto> GetIntelligenceManifestAsync(CancellationToken cancellationToken = default)
        => RequireResult(await SendAuthorizedRequestAsync(
            "api/v1/ai/intelligence/manifest",
            WinoAccountApiJsonContext.Default.ApiEnvelopeIntelligenceManifestDto,
            cancellationToken).ConfigureAwait(false), "Intelligence manifest request failed.");

    public async Task<IntelligenceMailboxStatusDto> GetIntelligenceStatusAsync(Guid mailboxId, CancellationToken cancellationToken = default)
        => RequireResult(await SendAuthorizedRequestAsync(
            $"api/v1/ai/intelligence/mailboxes/{mailboxId:D}/status",
            WinoAccountApiJsonContext.Default.ApiEnvelopeIntelligenceMailboxStatusDto,
            cancellationToken).ConfigureAwait(false), "Intelligence status request failed.");

    public async Task<IReadOnlyList<string>> ResolveIntelligenceDeltaAsync(
        Guid mailboxId,
        IReadOnlyList<string> remoteMessageIds,
        CancellationToken cancellationToken = default)
    {
        var account = await _databaseService.Connection.Table<WinoAccount>().FirstOrDefaultAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("A Wino account is required for mail intelligence.");
        var route = $"/api/v1/ai/intelligence/mailboxes/{mailboxId:D}/delta:resolve";
        using var response = await SendAuthorizedAsync(
            () => CreateAuthorizedRequestAsync(
                HttpMethod.Post,
                route.TrimStart('/'),
                () => new DeltaFrameContent(async (stream, token) =>
                {
                    var frameCount = Math.Max(1, (remoteMessageIds.Count + 999) / 1_000);
                    for (var sequence = 0; sequence < frameCount; sequence++)
                    {
                        var ids = remoteMessageIds.Skip(sequence * 1_000).Take(1_000).ToArray();
                        var frame = new IntelligenceDeltaFrameRequest(sequence, sequence == frameCount - 1, ids);
                        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
                            frame, WinoAccountApiJsonContext.Default.IntelligenceDeltaFrameRequest);
                        byte[]? encoded = null;
                        try
                        {
                            var encrypted = _contentEnvelopeEncryptor.Encrypt(
                                plaintext,
                                new ContentEnvelopeContext(account.Id, mailboxId, route),
                                Guid.NewGuid(),
                                DateTimeOffset.UtcNow);
                            try { encoded = ContentEnvelopeBinaryCodec.Encode(encrypted); }
                            finally
                            {
                                CryptographicOperations.ZeroMemory(encrypted.WrappedKey);
                                CryptographicOperations.ZeroMemory(encrypted.Nonce);
                                CryptographicOperations.ZeroMemory(encrypted.Tag);
                                CryptographicOperations.ZeroMemory(encrypted.Ciphertext);
                            }
                            var length = new byte[sizeof(int)];
                            BinaryPrimitives.WriteInt32BigEndian(length, encoded.Length);
                            await stream.WriteAsync(length, token).ConfigureAwait(false);
                            await stream.WriteAsync(encoded, token).ConfigureAwait(false);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(plaintext);
                            if (encoded is not null) CryptographicOperations.ZeroMemory(encoded);
                        }
                    }
                })),
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("MissingAccessToken");
        response.EnsureSuccessStatusCode();
        var missing = new List<string>();
        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(responseStream);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var frame = JsonSerializer.Deserialize(line, WinoAccountApiJsonContext.Default.IntelligenceDeltaFrameResult)
                ?? throw new InvalidOperationException("The intelligence delta response is invalid.");
            missing.AddRange(frame.MissingRemoteMessageIds);
            if (frame.IsFinal) break;
        }
        return missing;
    }

    public async Task<IntelligenceIngestResultDto> IngestIntelligenceAsync(
        Guid mailboxId,
        byte[] encryptedEnvelope,
        CancellationToken cancellationToken = default)
    {
        var result = await SendEncryptedIntelligenceAsync(
            $"api/v1/ai/intelligence/mailboxes/{mailboxId:D}/ingest",
            encryptedEnvelope,
            WinoAccountApiJsonContext.Default.ApiEnvelopeCompactIntelligenceIngestResultDto,
            "Intelligence ingestion failed.",
            cancellationToken).ConfigureAwait(false);

        return new IntelligenceIngestResultDto(
            result.Items,
            result.Artifacts.Select(static artifact => artifact.ToContract()).ToArray());
    }

    public async Task<IntelligenceArtifactCursorPageDto> GetIntelligenceArtifactsAsync(
        Guid mailboxId,
        string? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var page = RequireResult(await SendAuthorizedRequestAsync(
            $"api/v1/ai/intelligence/mailboxes/{mailboxId:D}/artifacts?cursor={Uri.EscapeDataString(cursor ?? string.Empty)}&pageSize={pageSize}",
            WinoAccountApiJsonContext.Default.ApiEnvelopeCompactIntelligenceArtifactCursorPageDto,
            cancellationToken).ConfigureAwait(false), "Intelligence artifact download failed.");

        return new IntelligenceArtifactCursorPageDto(
            page.NextCursor,
            page.Items.Select(static artifact => artifact.ToContract()).ToArray());
    }

    public async Task<IntelligenceMailboxStatusDto> RebuildIntelligenceEmbeddingsAsync(
        Guid mailboxId,
        CancellationToken cancellationToken = default)
        => RequireResult(await SendAuthorizedRequestAsync(
            HttpMethod.Post,
            $"api/v1/ai/intelligence/mailboxes/{mailboxId:D}/embeddings:rebuild",
            WinoAccountApiJsonContext.Default.ApiEnvelopeIntelligenceMailboxStatusDto,
            cancellationToken).ConfigureAwait(false), "Embedding rebuild request failed.");

    private async Task<T> SendEncryptedIntelligenceAsync<T>(
        string endpoint,
        byte[] encryptedEnvelope,
        JsonTypeInfo<ApiEnvelope<T>> responseType,
        string failureMessage,
        CancellationToken cancellationToken) where T : class
    {
        const int maximumAttempts = 5;
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
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
                if (attempt < maximumAttempts - 1 && response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.Conflict or
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
            catch (HttpRequestException) when (attempt < maximumAttempts - 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException($"{failureMessage} Retry limit reached.");
    }

    public Task<IntelligenceSemanticSearchResultDto> SearchIntelligenceAsync(byte[] encryptedEnvelope, CancellationToken cancellationToken = default)
        => SendEncryptedIntelligenceAsync(
            "api/v1/ai/intelligence/search",
            encryptedEnvelope,
            WinoAccountApiJsonContext.Default.ApiEnvelopeIntelligenceSemanticSearchResultDto,
            "Semantic intelligence search failed.",
            cancellationToken);

    public async Task<IntelligenceSemanticSearchResultDto> SearchIntelligenceAsync(
        IntelligenceSemanticSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var account = await _databaseService.Connection.Table<WinoAccount>().FirstOrDefaultAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("A Wino account is required for semantic search.");
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            request,
            WinoAccountApiJsonContext.Default.IntelligenceSemanticSearchRequest);
        const string route = "/api/v1/ai/intelligence/search";
        byte[]? encodedEnvelope = null;
        EncryptedContentEnvelope? encryptedEnvelope = null;
        try
        {
            encryptedEnvelope = _contentEnvelopeEncryptor.Encrypt(
                plaintext,
                new ContentEnvelopeContext(account.Id, Guid.Empty, route),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow);
            encodedEnvelope = ContentEnvelopeBinaryCodec.Encode(encryptedEnvelope);
            return await SearchIntelligenceAsync(encodedEnvelope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (encodedEnvelope is not null) CryptographicOperations.ZeroMemory(encodedEnvelope);
            if (encryptedEnvelope is not null)
            {
                CryptographicOperations.ZeroMemory(encryptedEnvelope.WrappedKey);
                CryptographicOperations.ZeroMemory(encryptedEnvelope.Nonce);
                CryptographicOperations.ZeroMemory(encryptedEnvelope.Tag);
                CryptographicOperations.ZeroMemory(encryptedEnvelope.Ciphertext);
            }
        }
    }

    public async Task<HeadlineTranslationResultDto> TranslateBriefingHeadlinesAsync(
        Guid mailboxId,
        string targetLanguage,
        CancellationToken cancellationToken = default)
        => RequireResult(await SendAuthorizedRequestAsync(
            HttpMethod.Post,
            $"api/v1/ai/intelligence/mailboxes/{mailboxId:D}/headlines:translate",
            new HeadlineTranslationRequest(targetLanguage),
            WinoAccountApiJsonContext.Default.HeadlineTranslationRequest,
            WinoAccountApiJsonContext.Default.ApiEnvelopeHeadlineTranslationResultDto,
            cancellationToken).ConfigureAwait(false), "Headline translation failed.");

    public async Task<WinoSuggestedRepliesResult> GetSuggestedRepliesAsync(
        Guid mailboxId,
        WinoSuggestedRepliesRequest request,
        Guid requestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var account = await _databaseService.Connection.Table<WinoAccount>().FirstOrDefaultAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("A Wino account is required for suggested replies.");
        var wireRequest = new LocalizedSuggestedRepliesRequest(
            request.Target,
            request.Thread,
            request.CandidateExamples,
            GetApplicationLanguage(),
            request.Tone,
            request.Count);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            wireRequest,
            WinoAccountApiJsonContext.Default.LocalizedSuggestedRepliesRequest);
        var route = $"/api/v1/ai/intelligence/mailboxes/{mailboxId:D}/suggested-replies";
        byte[]? encodedEnvelope = null;
        EncryptedContentEnvelope? encryptedEnvelope = null;
        try
        {
            encryptedEnvelope = _contentEnvelopeEncryptor.Encrypt(
                plaintext,
                new ContentEnvelopeContext(account.Id, mailboxId, route),
                requestId,
                DateTimeOffset.UtcNow);
            encodedEnvelope = ContentEnvelopeBinaryCodec.Encode(encryptedEnvelope);
            using var response = await SendAuthorizedAsync(
                () => CreateAuthorizedRequestAsync(
                    HttpMethod.Post,
                    route.TrimStart('/'),
                    () =>
                    {
                        var content = new ByteArrayContent(encodedEnvelope);
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                        return content;
                    }),
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("MissingAccessToken");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var envelope = await JsonSerializer.DeserializeAsync(
                stream,
                WinoAccountApiJsonContext.Default.ApiEnvelopeWinoSuggestedRepliesResult,
                cancellationToken).ConfigureAwait(false);
            return RequireResult(
                envelope ?? ApiEnvelope<WinoSuggestedRepliesResult>.Failure($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim()),
                "Suggested replies request failed.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (encodedEnvelope is not null) CryptographicOperations.ZeroMemory(encodedEnvelope);
            if (encryptedEnvelope is not null)
            {
                CryptographicOperations.ZeroMemory(encryptedEnvelope.WrappedKey);
                CryptographicOperations.ZeroMemory(encryptedEnvelope.Nonce);
                CryptographicOperations.ZeroMemory(encryptedEnvelope.Tag);
                CryptographicOperations.ZeroMemory(encryptedEnvelope.Ciphertext);
            }
        }
    }

    public async Task DeleteIntelligenceAsync(Guid mailboxId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthorizedAsync(
            () => CreateAuthorizedRequestAsync(HttpMethod.Delete, $"api/v1/ai/intelligence/mailboxes/{mailboxId:D}"),
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("MissingAccessToken");
        await EnsureSuccessResponseAsync(response, cancellationToken).ConfigureAwait(false);
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
            ApiErrorCodes.SemanticMailboxLimitExceeded => Translator.SemanticIndex_MailboxLimitExceeded,
            ApiErrorCodes.SemanticIndexStorageLimitExceeded => Translator.SemanticIndex_StorageLimitExceeded,
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

    private string GetApplicationLanguage()
    {
        var language = _translationService?.CurrentLanguageModel?.Code;
        if (!string.IsNullOrWhiteSpace(language))
            return language;

        return string.IsNullOrWhiteSpace(CultureInfo.CurrentUICulture.Name)
            ? "en-US"
            : CultureInfo.CurrentUICulture.Name;
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
[JsonSerializable(typeof(ApiEnvelope<List<SemanticMailboxDto>>))]
[JsonSerializable(typeof(ApiEnvelope<SemanticMailboxDto>))]
[JsonSerializable(typeof(EnsureSemanticMailboxRequest))]
[JsonSerializable(typeof(HeadlineTranslationRequest))]
[JsonSerializable(typeof(LocalizedSuggestedRepliesRequest))]
[JsonSerializable(typeof(IndexIntelligenceRequest))]
[JsonSerializable(typeof(GenerateInsightsRequest))]
[JsonSerializable(typeof(IntelligenceDeltaFrameRequest))]
[JsonSerializable(typeof(IntelligenceDeltaFrameResult))]
[JsonSerializable(typeof(IngestIntelligenceRequest))]
[JsonSerializable(typeof(IntelligenceIngestDocumentRequest))]
[JsonSerializable(typeof(IntelligenceCoverageRequest))]
[JsonSerializable(typeof(IntelligenceMetadataUpdateRequest))]
[JsonSerializable(typeof(IntelligenceDeleteMessagesRequest))]
[JsonSerializable(typeof(IntelligenceUpgradeRequest))]
[JsonSerializable(typeof(IntelligenceSemanticSearchRequest))]
[JsonSerializable(typeof(List<IntelligenceIndexDocumentRequest>))]
[JsonSerializable(typeof(IntelligenceIndexDocumentRequest))]
[JsonSerializable(typeof(ApiEnvelope<IntelligenceManifestDto>))]
[JsonSerializable(typeof(ApiEnvelope<IntelligenceMailboxStatusDto>))]
[JsonSerializable(typeof(ApiEnvelope<IntelligenceIngestResultDto>))]
[JsonSerializable(typeof(ApiEnvelope<CompactIntelligenceIngestResultDto>))]
[JsonSerializable(typeof(ApiEnvelope<IntelligenceArtifactCursorPageDto>))]
[JsonSerializable(typeof(ApiEnvelope<CompactIntelligenceArtifactCursorPageDto>))]
[JsonSerializable(typeof(ApiEnvelope<IntelligenceMailboxStateDto>))]
[JsonSerializable(typeof(ApiEnvelope<IntelligenceStageResultDto>))]
[JsonSerializable(typeof(ApiEnvelope<IntelligenceCoverageResultDto>))]
[JsonSerializable(typeof(ApiEnvelope<IntelligenceArtifactPageDto>))]
[JsonSerializable(typeof(IntelligenceArtifactDto))]
[JsonSerializable(typeof(SmartLabelsCapabilityPayload))]
[JsonSerializable(typeof(SimilarMessagesCapabilityPayload))]
[JsonSerializable(typeof(BriefingFactCapabilityPayload))]
[JsonSerializable(typeof(BriefingHeadlineCapabilityPayload))]
[JsonSerializable(typeof(SuggestedRepliesCapabilityPayload))]
[JsonSerializable(typeof(ApiEnvelope<IntelligenceSemanticSearchResultDto>))]
[JsonSerializable(typeof(ApiEnvelope<WinoSuggestedRepliesResult>))]
[JsonSerializable(typeof(ApiEnvelope<HeadlineTranslationResultDto>))]
[JsonSerializable(typeof(TransportConsentDto))]
[JsonSerializable(typeof(UpdateTransportConsentRequest))]
[JsonSerializable(typeof(MailboxProcessConsentDto))]
[JsonSerializable(typeof(ProcessConsentListDto))]
[JsonSerializable(typeof(UpdateProcessConsentRequest))]
[JsonSerializable(typeof(RevokeProcessConsentRequest))]
[JsonSerializable(typeof(BatchProcessConsentRequest))]
[JsonSerializable(typeof(BatchProcessConsentResult))]
[JsonSerializable(typeof(List<MailboxProcessConsentDto>))]
[JsonSerializable(typeof(ApiEnvelope<TransportConsentDto>))]
[JsonSerializable(typeof(ApiEnvelope<MailboxProcessConsentDto>))]
[JsonSerializable(typeof(ApiEnvelope<ProcessConsentListDto>))]
[JsonSerializable(typeof(ApiEnvelope<BatchProcessConsentResult>))]
internal sealed partial class WinoAccountApiJsonContext : JsonSerializerContext;

internal sealed record CompactIntelligenceArtifactCursorPageDto(
    string? NextCursor,
    IReadOnlyList<CompactIntelligenceArtifactDto> Items);

internal sealed record CompactIntelligenceIngestResultDto(
    IReadOnlyList<IntelligenceIngestItemResultDto> Items,
    IReadOnlyList<CompactIntelligenceArtifactDto> Artifacts);

internal sealed class CompactIntelligenceArtifactDto
{
    public required string RemoteMessageId { get; init; }
    public required string ContentHash { get; init; }
    public required IntelligenceCapability Capability { get; init; }
    public required int GenerationVersion { get; init; }
    public required int PayloadSchemaVersion { get; init; }
    public required long ArtifactRevision { get; init; }
    public required DateTimeOffset GeneratedAtUtc { get; init; }
    public bool IsDeleted { get; init; }
    public double? Confidence { get; init; }
    public SmartLabelsCapabilityPayload? SmartLabels { get; init; }
    public SimilarMessagesCapabilityPayload? SimilarMessages { get; init; }
    public BriefingFactCapabilityPayload? BriefingFact { get; init; }
    public BriefingHeadlineCapabilityPayload? BriefingHeadline { get; init; }
    public SuggestedRepliesCapabilityPayload? SuggestedReplies { get; init; }

    public IntelligenceArtifactDto ToContract() => new()
    {
        RemoteMessageId = RemoteMessageId,
        ContentHash = ContentHash,
        Capability = Capability,
        GenerationVersion = GenerationVersion,
        PayloadSchemaVersion = PayloadSchemaVersion,
        ArtifactRevision = ArtifactRevision,
        GeneratedAtUtc = GeneratedAtUtc,
        IsDeleted = IsDeleted,
        Confidence = Confidence,
        SmartLabels = SmartLabels,
        SimilarMessages = SimilarMessages,
        BriefingFact = BriefingFact,
        BriefingHeadline = BriefingHeadline,
        SuggestedReplies = SuggestedReplies,
    };
}
