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

namespace Wino.Services;

public sealed class WinoAccountProfileService : BaseDatabaseService, IWinoAccountProfileService
{
    private readonly IWinoAccountApiClient _apiClient;
    private readonly ITranslationService? _translationService;
    private readonly SemaphoreSlim _tokenRefreshLock = new(1, 1);
    private readonly ILogger _logger = Log.ForContext<WinoAccountProfileService>();

    public WinoAccountProfileService(IDatabaseService databaseService,
                                     IWinoAccountApiClient apiClient,
                                     ITranslationService? translationService = null) : base(databaseService)
    {
        _apiClient = apiClient;
        _translationService = translationService;
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
        await _tokenRefreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var account = await GetActiveAccountAsync().ConfigureAwait(false);
            if (account == null || string.IsNullOrWhiteSpace(account.RefreshToken))
            {
                _logger.Warning("Wino account token refresh skipped because there is no active account or refresh token.");
                return WinoAccountOperationResult.Failure(ApiErrorCodes.RefreshTokenInvalid);
            }

            if (!string.IsNullOrWhiteSpace(account.AccessToken) && account.AccessTokenExpiresAtUtc > DateTime.UtcNow)
            {
                return WinoAccountOperationResult.Success(account);
            }

            _logger.Information("Refreshing Wino account token for {Email}", account.Email);
            var response = await _apiClient.RefreshAsync(account.RefreshToken, cancellationToken).ConfigureAwait(false);
            var result = await PersistResponseAsync(response).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                _logger.Warning("Wino account token refresh failed for {Email}. Error code: {ErrorCode}", account.Email, result.ErrorCode);
                return result;
            }

            if (result.Account != null && !AreEquivalentProfiles(account, result.Account))
            {
                PublishProfileUpdated(result.Account);
            }

            return result;
        }
        finally
        {
            _tokenRefreshLock.Release();
        }
    }

    public async Task<WinoAccountOperationResult> RefreshProfileAsync(CancellationToken cancellationToken = default)
    {
        var account = await GetAuthenticatedAccountAsync(cancellationToken).ConfigureAwait(false);
        if (account == null)
        {
            return WinoAccountOperationResult.Failure("MissingAccessToken");
        }

        var response = await _apiClient.GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess || response.Result == null)
        {
            _logger.Warning("Failed to refresh Wino account profile for {Email}. Error code: {ErrorCode}", account.Email, response.ErrorCode);
            return WinoAccountOperationResult.Failure(response.ErrorCode);
        }

        var refreshedAccount = MergeAccountProfile(account, response.Result);

        if (AreEquivalentProfiles(account, refreshedAccount))
        {
            return WinoAccountOperationResult.Success(account);
        }

        await PersistAccountAsync(refreshedAccount).ConfigureAwait(false);
        PublishProfileUpdated(refreshedAccount);

        return WinoAccountOperationResult.Success(refreshedAccount);
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
        var account = await GetAuthenticatedAccountAsync(cancellationToken).ConfigureAwait(false);
        if (account == null)
        {
            return ApiEnvelope<AuthUserDto>.Failure("MissingAccessToken");
        }

        var response = await _apiClient.GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            _logger.Warning("Failed to load Wino account profile for {Email}. Error code: {ErrorCode}", account.Email, response.ErrorCode);
            return response;
        }

        if (response.Result != null)
        {
            var refreshedAccount = MergeAccountProfile(account, response.Result);
            await PersistProfileDataAsync(account, refreshedAccount).ConfigureAwait(false);
        }

        return response;
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

        await Connection.DeleteAllAsync<WinoAccount>().ConfigureAwait(false);
        if (account != null)
        {
            ReportUIChange(new WinoAccountProfileDeletedMessage(account));
            ReportUIChange(new WinoAccountSignedOutMessage(account));
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
        await Connection.DeleteAllAsync<WinoAccount>().ConfigureAwait(false);
        await Connection.InsertOrReplaceAsync(account, typeof(WinoAccount)).ConfigureAwait(false);
    }

    private async Task PersistProfileDataAsync(WinoAccount originalAccount, WinoAccount refreshedAccount)
    {
        if (!AreEquivalentProfiles(originalAccount, refreshedAccount))
        {
            await PersistAccountAsync(refreshedAccount).ConfigureAwait(false);
            PublishProfileUpdated(refreshedAccount);
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
