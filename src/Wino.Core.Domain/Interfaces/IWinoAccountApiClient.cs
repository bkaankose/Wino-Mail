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
    // Mail intelligence. Jobs are submitted per mailbox; results are collected as two
    // independently downloadable and independently acknowledged stages.
    Task<MailIntelligenceJobAcceptedDto> SubmitMailIntelligenceJobAsync(
        Guid mailboxId, Guid jobId, string checksum, byte[] upload, CancellationToken cancellationToken = default);
    Task<MailIntelligenceJobListDto> GetMailIntelligenceJobsAsync(CancellationToken cancellationToken = default);
    Task<MailIntelligenceJobDto?> GetMailIntelligenceJobAsync(Guid mailboxId, Guid jobId, CancellationToken cancellationToken = default);
    Task<ClassificationResultPageDto> GetClassificationResultPageAsync(Guid mailboxId, Guid jobId, int page, CancellationToken cancellationToken = default);
    Task<SummaryResultPageDto> GetEnrichmentResultPageAsync(Guid mailboxId, Guid jobId, int page, CancellationToken cancellationToken = default);
    Task<MailIntelligenceStageAckResultDto> AcknowledgeMailIntelligenceStageAsync(
        Guid mailboxId, Guid jobId, string stage, string digest, CancellationToken cancellationToken = default);
    Task CancelMailIntelligenceJobAsync(Guid mailboxId, Guid jobId, CancellationToken cancellationToken = default);
    Task<AnalyzeMailResponseDto> AnalyzeMailAsync(
        Guid mailboxId, byte[] encryptedEnvelope, string language, CancellationToken cancellationToken = default);
    Task<IntelligenceConsentDto> GetIntelligenceConsentAsync(CancellationToken cancellationToken = default);
    Task<IntelligenceConsentDto> AcceptIntelligenceConsentAsync(string policyVersion, string source, CancellationToken cancellationToken = default);
    Task<IntelligenceConsentDto> RevokeIntelligenceConsentAsync(string source, CancellationToken cancellationToken = default);
}
