#nullable enable
using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Api.Contracts.Users;
using Wino.Mail.AI.Abstractions;
using System.Collections.Generic;

namespace Wino.Core.Domain.Interfaces;

public interface IWinoAccountProfileService
{
    Task<WinoAccountOperationResult> RegisterAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<WinoAccountOperationResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<WinoAccountOperationResult> RefreshAsync(CancellationToken cancellationToken = default);
    Task<WinoAccountOperationResult> RefreshProfileAsync(CancellationToken cancellationToken = default);
    Task<ApiEnvelope<EmailConfirmationResendResultDto>> ResendEmailConfirmationAsync(string endpoint, string ticket, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<JsonElement>> ForgotPasswordAsync(string email, CancellationToken cancellationToken = default);
    Task<WinoAccount?> GetActiveAccountAsync();
    Task<WinoAccount?> GetAuthenticatedAccountAsync(CancellationToken cancellationToken = default);
    Task<bool> HasActiveAccountAsync();
    Task<ApiEnvelope<AuthUserDto>> GetCurrentUserAsync(CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AiSummaryResultDto>> SummarizeAsync(IReadOnlyList<MailContentSegment> segments, string targetLanguage, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AiTranslationResultDto>> TranslateAsync(IReadOnlyList<MailContentSegment> segments, string? sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AiTextResultDto>> RewriteAsync(string html, string mode, CancellationToken cancellationToken = default);
    Task<string?> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(string settingsJson, CancellationToken cancellationToken = default);
    Task<UserMailboxSyncListDto> GetMailboxesAsync(CancellationToken cancellationToken = default);
    Task ReplaceMailboxesAsync(ReplaceUserMailboxesRequestDto request, CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
}
