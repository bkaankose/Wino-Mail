#nullable enable
using System.Text.Json;
using Wino.AppServices.Contracts.Generated;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Api.Contracts.Users;
using Wino.Services;

namespace Wino.Mail.Uwp.Services;

/// <summary>
/// Keeps the two pure SQLite profile lookups in UWP. Token validation, refresh,
/// network calls and every mutation remain companion-owned.
/// </summary>
internal sealed class WinoAccountProfileHybridService(
    IDatabaseService database,
    WinoAccountProfileServiceRemoteProxy remote) : IWinoAccountProfileService
{
    public async Task<WinoAccount?> GetActiveAccountAsync()
    {
        if (!database.IsAvailable)
        {
            return null;
        }

        return await database.Connection.Table<WinoAccount>().FirstOrDefaultAsync().ConfigureAwait(false);
    }

    public async Task<bool> HasActiveAccountAsync() =>
        database.IsAvailable &&
        await database.Connection.Table<WinoAccount>().CountAsync().ConfigureAwait(false) > 0;

    public Task<WinoAccountOperationResult> RegisterAsync(string email, string password, CancellationToken cancellationToken = default) =>
        remote.RegisterAsync(email, password, cancellationToken);
    public Task<WinoAccountOperationResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default) =>
        remote.LoginAsync(email, password, cancellationToken);
    public Task<WinoAccountOperationResult> RefreshAsync(CancellationToken cancellationToken = default) =>
        remote.RefreshAsync(cancellationToken);
    public Task<WinoAccountOperationResult> RefreshProfileAsync(CancellationToken cancellationToken = default) =>
        remote.RefreshProfileAsync(cancellationToken);
    public Task<ApiEnvelope<EmailConfirmationResendResultDto>> ResendEmailConfirmationAsync(string endpoint, string ticket, CancellationToken cancellationToken = default) =>
        remote.ResendEmailConfirmationAsync(endpoint, ticket, cancellationToken);
    public Task<ApiEnvelope<JsonElement>> ForgotPasswordAsync(string email, CancellationToken cancellationToken = default) =>
        remote.ForgotPasswordAsync(email, cancellationToken);
    public Task<WinoAccount?> GetAuthenticatedAccountAsync(CancellationToken cancellationToken = default) =>
        remote.GetAuthenticatedAccountAsync(cancellationToken);
    public Task<ApiEnvelope<AuthUserDto>> GetCurrentUserAsync(CancellationToken cancellationToken = default) =>
        remote.GetCurrentUserAsync(cancellationToken);
    public Task<ApiEnvelope<AiStatusResultDto>> GetAiStatusAsync(CancellationToken cancellationToken = default) =>
        remote.GetAiStatusAsync(cancellationToken);
    public Task<ApiEnvelope<AiTextResultDto>> SummarizeAsync(string html, string targetLanguage, CancellationToken cancellationToken = default) =>
        remote.SummarizeAsync(html, targetLanguage, cancellationToken);
    public Task<ApiEnvelope<AiTextResultDto>> TranslateAsync(string html, string targetLanguage, CancellationToken cancellationToken = default) =>
        remote.TranslateAsync(html, targetLanguage, cancellationToken);
    public Task<ApiEnvelope<AiTextResultDto>> RewriteAsync(string html, string mode, CancellationToken cancellationToken = default) =>
        remote.RewriteAsync(html, mode, cancellationToken);
    public Task<ApiEnvelope<JsonElement>> SyncStoreEntitlementsAsync(CancellationToken cancellationToken = default) =>
        remote.SyncStoreEntitlementsAsync(cancellationToken);
    public Task<string?> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        remote.GetSettingsAsync(cancellationToken);
    public Task SaveSettingsAsync(string settingsJson, CancellationToken cancellationToken = default) =>
        remote.SaveSettingsAsync(settingsJson, cancellationToken);
    public Task<UserMailboxSyncListDto> GetMailboxesAsync(CancellationToken cancellationToken = default) =>
        remote.GetMailboxesAsync(cancellationToken);
    public Task ReplaceMailboxesAsync(ReplaceUserMailboxesRequestDto request, CancellationToken cancellationToken = default) =>
        remote.ReplaceMailboxesAsync(request, cancellationToken);
    public Task<bool> ProcessBillingCallbackAsync(Uri callbackUri, CancellationToken cancellationToken = default) =>
        remote.ProcessBillingCallbackAsync(callbackUri, cancellationToken);
    public Task SignOutAsync(CancellationToken cancellationToken = default) =>
        remote.SignOutAsync(cancellationToken);
}
