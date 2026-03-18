#nullable enable
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.Api.Contracts.Common;

namespace Wino.Core.Domain.Interfaces;

public interface IWinoAccountApiClient
{
    Task<ApiEnvelope<AuthResultDto>> RegisterAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AuthResultDto>> LoginAsync(string email, string password, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AuthResultDto>> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<JsonElement>> LogoutAsync(string refreshToken, CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AuthUserDto>> GetCurrentUserAsync(CancellationToken cancellationToken = default);
    Task<ApiEnvelope<AiStatusResultDto>> GetAiStatusAsync(CancellationToken cancellationToken = default);
    Task<string?> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<bool> SaveSettingsAsync(string settingsJson, CancellationToken cancellationToken = default);
}
