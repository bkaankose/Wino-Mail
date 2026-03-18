#nullable enable
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
    Task<WinoAccount?> GetActiveAccountAsync();
    Task<WinoAccount?> GetAuthenticatedAccountAsync(CancellationToken cancellationToken = default);
    Task<bool> HasActiveAccountAsync();
    Task<ApiEnvelope<AuthUserDto>> GetCurrentUserAsync(CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AiStatusResultDto>> GetAiStatusAsync(CancellationToken cancellationToken = default);
    Task<ApiEnvelope<string>> CreateCheckoutSessionAsync(string productId, CancellationToken cancellationToken = default);
    Task SignOutAsync(CancellationToken cancellationToken = default);
}
