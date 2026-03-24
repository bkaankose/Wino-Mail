#nullable enable
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.Api.Contracts.Billing;
using Wino.Mail.Api.Contracts.Common;

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
    Task<ApiEnvelope<AiStatusResultDto>> GetAiStatusAsync(CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AiTextResultDto>> SummarizeAsync(string html, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AiTextResultDto>> TranslateAsync(string html, string targetLanguage, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AiTextResultDto>> RewriteAsync(string html, string mode, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<CheckoutSessionResultDto>> CreateCheckoutSessionAsync(WinoAddOnProductType productId, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<CustomerPortalResultDto>> CreateCustomerPortalSessionAsync(CancellationToken cancellationToken = default);
    Task<string?> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<bool> SaveSettingsAsync(string settingsJson, CancellationToken cancellationToken = default);
}
