#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Api.Contracts.Users;
using Wino.Messaging.UI;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.Contracts.Intelligence;
using Wino.Mail.Contracts.SemanticIndex;

namespace Wino.Services;

public sealed class WinoAccountProfileService : BaseDatabaseService, IWinoAccountProfileService
{
    private readonly IWinoAccountApiClient _apiClient;
    private readonly ITranslationService? _translationService;
    private readonly ISemanticIndexCoordinator? _semanticIndexCoordinator;
    private readonly ILocalIntelligenceStore? _localIntelligenceStore;
    private readonly IWinoAccountSessionService _sessions;
    private readonly IWinoPendingCheckoutStore? _pendingCheckouts;
    private readonly ILogger _logger = Log.ForContext<WinoAccountProfileService>();

    public WinoAccountProfileService(IDatabaseService databaseService,
                                     IWinoAccountApiClient apiClient,
                                     ITranslationService? translationService = null,
                                     ISemanticIndexCoordinator? semanticIndexCoordinator = null,
                                     ILocalIntelligenceStore? localIntelligenceStore = null,
                                     IWinoAccountSessionService? sessionService = null,
                                     IWinoPendingCheckoutStore? pendingCheckouts = null) : base(databaseService)
    {
        _apiClient = apiClient;
        _translationService = translationService;
        _semanticIndexCoordinator = semanticIndexCoordinator;
        _localIntelligenceStore = localIntelligenceStore;
        _sessions = sessionService ?? WinoAccountSessionService.For(databaseService);
        _pendingCheckouts = pendingCheckouts;
    }

    public async Task<WinoAccountOperationResult> RegisterAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        var response = await _apiClient.RegisterAsync(email, password, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess || response.Result == null)
        {
            _logger.Warning("Wino account registration failed. Error code: {ErrorCode}. Error message: {ErrorMessage}", response.ErrorCode, response.ErrorMessage);
            return WinoAccountOperationResult.Failure(response.ErrorCode, response.ErrorMessage, response.ErrorDetails);
        }

        // Registration no longer signs the user in locally until the email address is confirmed.
        return WinoAccountOperationResult.Success(Map(response.Result));
    }

    public async Task<WinoAccountOperationResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        var response = await _apiClient.LoginAsync(email, password, cancellationToken).ConfigureAwait(false);
        var result = await PersistResponseAsync(response).ConfigureAwait(false);

        if (result.IsSuccess && result.Account != null)
        {
            PublishProfileUpdated(result.Account);
            ReportUIChange(new WinoAccountSignedInMessage(result.Account));
        }

        return result;
    }

    public Task<ApiEnvelope<EmailConfirmationResendResultDto>> ResendEmailConfirmationAsync(string endpoint, string ticket, CancellationToken cancellationToken = default)
        => _apiClient.ResendEmailConfirmationAsync(endpoint, ticket, cancellationToken);

    public Task<ApiEnvelope<JsonElement>> ForgotPasswordAsync(string email, CancellationToken cancellationToken = default)
        => _apiClient.ForgotPasswordAsync(email, cancellationToken);

    public async Task<WinoAccountOperationResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var session = await _sessions.CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (session is null) return WinoAccountOperationResult.Failure(ApiErrorCodes.RefreshTokenInvalid);

        WinoAccountOperationResult? failure = null;
        WinoAccount? original = null;
        var refreshed = await _sessions.RefreshCredentialsAsync(session, null, async (account, token) =>
        {
            original = account;
            var response = await _apiClient.RefreshAsync(account.RefreshToken, token).ConfigureAwait(false);
            if (!response.IsSuccess || response.Result is null)
            {
                failure = WinoAccountOperationResult.Failure(response.ErrorCode, response.ErrorMessage, response.ErrorDetails);
                return null;
            }

            return Map(response.Result);
        }, cancellationToken).ConfigureAwait(false);

        if (refreshed is null) return failure ?? WinoAccountOperationResult.Failure(ApiErrorCodes.RefreshTokenInvalid);
        await _sessions.CommitAsync(session, () =>
        {
            if (original is not null && !AreEquivalentProfiles(original, refreshed))
                PublishProfileUpdated(refreshed);
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        return WinoAccountOperationResult.Success(refreshed);
    }

    public async Task<WinoAccountOperationResult> RefreshProfileAsync(CancellationToken cancellationToken = default)
    {
        var response = await GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess || response.Result is null)
            return WinoAccountOperationResult.Failure(response.ErrorCode);

        var account = await GetActiveAccountAsync().ConfigureAwait(false);
        return account is not null && account.Id == response.Result.UserId
            ? WinoAccountOperationResult.Success(account)
            : WinoAccountOperationResult.Failure("AccountSessionChanged");
    }

    public async Task<WinoAccount?> GetActiveAccountAsync()
    {
        var account = await Connection.Table<WinoAccount>().FirstOrDefaultAsync().ConfigureAwait(false);
        return account;
    }

    public async Task<WinoAccount?> GetAuthenticatedAccountAsync(CancellationToken cancellationToken = default)
    {
        var account = await GetActiveAccountAsync().ConfigureAwait(false);

        if (account == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(account.AccessToken))
        {
            _logger.Warning("Wino account {Email} is missing an access token.", account.Email);
            return null;
        }

        if (account.AccessTokenExpiresAtUtc > DateTime.UtcNow)
        {
            return account;
        }

        var refreshResult = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (!refreshResult.IsSuccess)
        {
            return null;
        }

        return refreshResult.Account ?? await GetActiveAccountAsync().ConfigureAwait(false);
    }

    public async Task<bool> HasActiveAccountAsync()
        => await Connection.Table<WinoAccount>().CountAsync().ConfigureAwait(false) > 0;

    public async Task<ApiEnvelope<AuthUserDto>> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var session = await _sessions.CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (session is null || await GetAuthenticatedAccountAsync(cancellationToken).ConfigureAwait(false) is null)
            return ApiEnvelope<AuthUserDto>.Failure("MissingAccessToken");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.CancellationToken);
        var response = await _apiClient.GetCurrentUserAsync(linked.Token).ConfigureAwait(false);
        if (!response.IsSuccess || response.Result is null) return response;
        if (response.Result.UserId != session.AccountId) return ApiEnvelope<AuthUserDto>.Failure("AccountSessionChanged");

        var committed = await _sessions.CommitAsync(session, async () =>
        {
            var current = await GetActiveAccountAsync().ConfigureAwait(false);
            var refreshed = MergeAccountProfile(current!, response.Result);
            if (!AreEquivalentProfiles(current!, refreshed))
            {
                await Connection.UpdateAsync(refreshed, typeof(WinoAccount)).ConfigureAwait(false);
                PublishProfileUpdated(refreshed);
            }
        }, cancellationToken).ConfigureAwait(false);

        return committed ? response : ApiEnvelope<AuthUserDto>.Failure("AccountSessionChanged");
    }

    public async Task<ApiEnvelope<AiSummaryResultDto>> SummarizeAsync(IReadOnlyList<MailContentSegment> segments, string targetLanguage, CancellationToken cancellationToken = default)
        => await ExecuteAiOperationAsync(account => _apiClient.SummarizeAsync(segments, targetLanguage, cancellationToken), "summarize", cancellationToken).ConfigureAwait(false);

    public async Task<ApiEnvelope<AiTranslationResultDto>> TranslateAsync(IReadOnlyList<MailContentSegment> segments, string? sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
        => await ExecuteAiOperationAsync(account => _apiClient.TranslateAsync(segments, sourceLanguage, targetLanguage, cancellationToken), "translate", cancellationToken).ConfigureAwait(false);

    public async Task<ApiEnvelope<AiTextResultDto>> RewriteAsync(string html, string mode, CancellationToken cancellationToken = default)
        => await ExecuteAiOperationAsync(
            account => _apiClient.RewriteAsync(
                html,
                mode,
                _translationService?.CurrentLanguageModel?.Code ?? CultureInfo.CurrentUICulture.Name ?? "en-US",
                cancellationToken),
            "rewrite",
            cancellationToken).ConfigureAwait(false);

    public async Task<string?> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        _ = await GetAuthenticatedAccountAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("MissingAccessToken");

        return await _apiClient.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveSettingsAsync(string settingsJson, CancellationToken cancellationToken = default)
    {
        _ = await GetAuthenticatedAccountAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("MissingAccessToken");

        await _apiClient.SaveSettingsAsync(settingsJson, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserMailboxSyncListDto> GetMailboxesAsync(CancellationToken cancellationToken = default)
    {
        _ = await GetAuthenticatedAccountAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("MissingAccessToken");

        return await _apiClient.GetMailboxesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceMailboxesAsync(ReplaceUserMailboxesRequestDto request, CancellationToken cancellationToken = default)
    {
        _ = await GetAuthenticatedAccountAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("MissingAccessToken");

        await _apiClient.ReplaceMailboxesAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var account = await GetActiveAccountAsync().ConfigureAwait(false);

        // Account-owned local intelligence must be gone before local sign-out can succeed.
        await _sessions.ReplaceAsync(null, () => PurgeLocalIntelligenceAsync(cancellationToken), cancellationToken).ConfigureAwait(false);

        if (account != null)
        {
            ReportUIChange(new WinoAccountProfileDeletedMessage(account));
            ReportUIChange(new WinoAccountSignedOutMessage(account));
        }

        if (account != null && !string.IsNullOrWhiteSpace(account.RefreshToken))
        {
            try
            {
                var result = await _apiClient.LogoutAsync(account.RefreshToken, cancellationToken).ConfigureAwait(false);
                if (!result.IsSuccess && !string.IsNullOrWhiteSpace(result.ErrorCode))
                {
                    _logger.Warning("Wino account remote sign-out failed with error code {ErrorCode}", result.ErrorCode);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Wino account remote sign-out failed.");
            }
        }

    }

    private async Task<WinoAccountOperationResult> PersistResponseAsync(WinoAccountApiResult<AuthResultDto> response)
    {
        if (!response.IsSuccess || response.Result == null)
        {
            _logger.Warning("Wino account operation failed. Error code: {ErrorCode}. Error message: {ErrorMessage}", response.ErrorCode, response.ErrorMessage);
            return WinoAccountOperationResult.Failure(response.ErrorCode, response.ErrorMessage, response.ErrorDetails);
        }

        var account = Map(response.Result);

        await PersistAccountAsync(account).ConfigureAwait(false);

        return WinoAccountOperationResult.Success(account);
    }

    private async Task PersistAccountAsync(WinoAccount account)
    {
        await _sessions.ReplaceAsync(account, async () =>
        {
            var existingAccount = await GetActiveAccountAsync().ConfigureAwait(false);
            if (existingAccount is not null && existingAccount.Id != account.Id)
                await PurgeLocalIntelligenceAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task PurgeLocalIntelligenceAsync(CancellationToken cancellationToken = default)
    {
        var account = await GetActiveAccountAsync().ConfigureAwait(false);
        if (account is not null) _pendingCheckouts?.Clear(account.Id);

        if (_semanticIndexCoordinator != null)
        {
            await _semanticIndexCoordinator.ResetLocalStateAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_localIntelligenceStore != null)
        {
            await _localIntelligenceStore.DeleteDatabaseAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void PublishProfileUpdated(WinoAccount account)
        => ReportUIChange(new WinoAccountProfileUpdatedMessage(account));

    private async Task<ApiEnvelope<T>> ExecuteAiOperationAsync<T>(Func<WinoAccount, Task<ApiEnvelope<T>>> executeAsync,
                                                                   string operationName,
                                                                   CancellationToken cancellationToken)
    {
        var account = await GetAuthenticatedAccountAsync(cancellationToken).ConfigureAwait(false);
        if (account == null)
        {
            return ApiEnvelope<T>.Failure("MissingAccessToken");
        }

        var response = await executeAsync(account).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            _logger.Warning("Failed to {Operation} HTML with AI for Wino account {Email}. Error code: {ErrorCode}", operationName, account.Email, response.ErrorCode);
        }

        return response;
    }

    private static bool AreEquivalentProfiles(WinoAccount left, WinoAccount right)
        => left.Id == right.Id &&
           string.Equals(left.Email, right.Email, StringComparison.Ordinal) &&
           string.Equals(left.AccountStatus, right.AccountStatus, StringComparison.Ordinal) &&
           left.HasPassword == right.HasPassword &&
           left.HasGoogleLogin == right.HasGoogleLogin &&
           left.HasFacebookLogin == right.HasFacebookLogin &&
           left.IsUnlimitedAccountsEnabled == right.IsUnlimitedAccountsEnabled;

    private static WinoAccount MergeAccountProfile(WinoAccount existingAccount, AuthUserDto profile)
        => new()
        {
            Id = profile.UserId,
            Email = profile.Email,
            AccountStatus = profile.AccountStatus,
            HasPassword = profile.HasPassword,
            HasGoogleLogin = profile.HasGoogleLogin,
            HasFacebookLogin = profile.HasFacebookLogin,
            IsUnlimitedAccountsEnabled = profile.IsUnlimitedAccountsEnabled,
            AccessToken = existingAccount.AccessToken,
            AccessTokenExpiresAtUtc = existingAccount.AccessTokenExpiresAtUtc,
            RefreshToken = existingAccount.RefreshToken,
            RefreshTokenExpiresAtUtc = existingAccount.RefreshTokenExpiresAtUtc,
            LastAuthenticatedUtc = existingAccount.LastAuthenticatedUtc
        };

    private static WinoAccount Map(AuthResultDto result)
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
            LastAuthenticatedUtc = DateTime.UtcNow
        };
}
