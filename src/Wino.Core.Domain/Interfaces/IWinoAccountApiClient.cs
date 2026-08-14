#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.Api.Contracts.Billing;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Api.Contracts.Users;
using Wino.Mail.Contracts.SemanticIndex;
using Wino.Mail.Contracts.Intelligence;
using Wino.Mail.AI.Abstractions;

namespace Wino.Core.Domain.Interfaces;

public interface IWinoAccountApiClient
{
    Task<WinoAccountApiResult<AuthResultDto>> RegisterAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<WinoAccountApiResult<AuthResultDto>> LoginAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<WinoAccountApiResult<AuthResultDto>> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<EmailConfirmationResendResultDto>> ResendEmailConfirmationAsync(string endpoint, string ticket, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<JsonElement>> ForgotPasswordAsync(string email, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<JsonElement>> LogoutAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AuthUserDto>> GetCurrentUserAsync(CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AiSummaryResultDto>> SummarizeAsync(IReadOnlyList<MailContentSegment> segments, string targetLanguage, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AiTranslationResultDto>> TranslateAsync(IReadOnlyList<MailContentSegment> segments, string? sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AiTextResultDto>> RewriteAsync(string html, string mode, string language, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<CheckoutSessionResultDto>> CreateCheckoutSessionAsync(string productCode, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<BillingStatusResultDto>> GetBillingStatusAsync(CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AiUsageStatusDto>> GetAiUsageAsync(CancellationToken cancellationToken = default);
    Task<string?> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(string settingsJson, CancellationToken cancellationToken = default);
    Task<UserMailboxSyncListDto> GetMailboxesAsync(CancellationToken cancellationToken = default);
    Task ReplaceMailboxesAsync(ReplaceUserMailboxesRequestDto request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SemanticMailboxDto>> GetSemanticMailboxesAsync(CancellationToken cancellationToken = default);
    Task<SemanticMailboxDto> EnsureSemanticMailboxAsync(string address, int providerType, CancellationToken cancellationToken = default);
    Task<IntelligenceManifestDto> GetIntelligenceManifestAsync(CancellationToken cancellationToken = default);
    Task<IntelligenceMailboxStatusDto> GetIntelligenceStatusAsync(Guid mailboxId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ResolveIntelligenceDeltaAsync(Guid mailboxId, IReadOnlyList<string> remoteMessageIds, CancellationToken cancellationToken = default);
    Task<IntelligenceIngestResultDto> IngestIntelligenceAsync(Guid mailboxId, byte[] encryptedEnvelope, CancellationToken cancellationToken = default);
    Task<IntelligenceArtifactCursorPageDto> GetIntelligenceArtifactsAsync(Guid mailboxId, string? cursor, int pageSize, CancellationToken cancellationToken = default);
    Task<IntelligenceMailboxStatusDto> RebuildIntelligenceEmbeddingsAsync(Guid mailboxId, CancellationToken cancellationToken = default);
    Task<IntelligenceSemanticSearchResultDto> SearchIntelligenceAsync(IntelligenceSemanticSearchRequest request, CancellationToken cancellationToken = default);
    Task<IntelligenceSemanticSearchResultDto> SearchIntelligenceAsync(byte[] encryptedEnvelope, CancellationToken cancellationToken = default);
    Task<WinoSuggestedRepliesResult> GetSuggestedRepliesAsync(Guid mailboxId, WinoSuggestedRepliesRequest request, Guid requestId, CancellationToken cancellationToken = default);
    Task<HeadlineTranslationResultDto> TranslateBriefingHeadlinesAsync(Guid mailboxId, string targetLanguage, CancellationToken cancellationToken = default);
    Task DeleteIntelligenceAsync(Guid mailboxId, CancellationToken cancellationToken = default);
    Task<TransportConsentDto> GetTransportConsentAsync(CancellationToken cancellationToken = default);
    Task<TransportConsentDto> AcceptTransportConsentAsync(string policyVersion, string source, CancellationToken cancellationToken = default);
    Task<TransportConsentDto> RevokeTransportConsentAsync(string source, CancellationToken cancellationToken = default);
    Task<ProcessConsentListDto> GetProcessConsentsAsync(CancellationToken cancellationToken = default);
    Task<MailboxProcessConsentDto> AcceptProcessConsentAsync(Guid mailboxId, string policyVersion, string source, CancellationToken cancellationToken = default);
    Task<MailboxProcessConsentDto> RevokeProcessConsentAsync(Guid mailboxId, string source, CancellationToken cancellationToken = default);
    Task<BatchProcessConsentResult> UpdateProcessConsentsAsync(BatchProcessConsentRequest request, CancellationToken cancellationToken = default);
}
