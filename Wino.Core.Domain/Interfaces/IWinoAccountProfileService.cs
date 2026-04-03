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
    Task<ApiEnvelope<AiStatusResultDto>> GetAiStatusAsync(CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AiTextResultDto>> SummarizeAsync(string html, string targetLanguage, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AiTextResultDto>> TranslateAsync(string html, string targetLanguage, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AiTextResultDto>> RewriteAsync(string html, string mode, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<JsonElement>> SyncStoreEntitlementsAsync(CancellationToken cancellationToken = default);
    Task<bool> ProcessBillingCallbackAsync(Uri callbackUri, CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
}
