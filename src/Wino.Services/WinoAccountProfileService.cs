#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Api.Contracts.Users;
using Wino.Messaging.UI;

namespace Wino.Services;

public sealed class WinoAccountProfileService : BaseDatabaseService, IWinoAccountProfileService
{
    private readonly IWinoAccountApiClient _apiClient;
    private readonly ISyncSnapshotKeyService? _snapshotKeys;
    private readonly ITranslationService? _translationService;
    private readonly IMailIntelligenceCoordinator? _semanticIndexCoordinator;
    private readonly IMailIntelligenceStore? _localIntelligenceStore;
    private readonly IWinoAccountSessionService _sessions;
    private readonly IWinoPendingCheckoutStore? _pendingCheckouts;
    private readonly ILogger _logger = Log.ForContext<WinoAccountProfileService>();

    public WinoAccountProfileService(IDatabaseService databaseService,
                                     IWinoAccountApiClient apiClient,
                                     ITranslationService? translationService = null,
                                     IMailIntelligenceCoordinator? semanticIndexCoordinator = null,
                                     IMailIntelligenceStore? localIntelligenceStore = null,
                                     IWinoAccountSessionService? sessionService = null,
                                     IWinoPendingCheckoutStore? pendingCheckouts = null,
                                     ISyncSnapshotKeyService? snapshotKeys = null) : base(databaseService)
    {
        _snapshotKeys = snapshotKeys;
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
        return response.IsSuccess && response.Result is not null
            ? WinoAccountOperationResult.Success(Map(response.Result))
            : WinoAccountOperationResult.Failure(response.ErrorCode, response.ErrorMessage, response.ErrorDetails);
    }

    public async Task<WinoAccountOperationResult> RegisterWithProfileAsync(string email, string password, string? displayName, CancellationToken cancellationToken = default)
    {
        var response = await _apiClient.RegisterWithProfileAsync(email, password, displayName, cancellationToken).ConfigureAwait(false);
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
            : WinoAccountOperationResult.Failure(WinoAccountClientErrorCodes.AccountSessionChanged);
    }

    public async Task<WinoAccount?> GetActiveAccountAsync()
    {
        var account = await Connection.Table<WinoAccount>().FirstOrDefaultAsync().ConfigureAwait(false);
        return account;
    }

    public async Task<WinoAccount?> GetAuthenticatedAccountAsync(CancellationToken cancellationToken = default)
    {
        var (account, errorCode) = await AuthenticateAsync(cancellationToken).ConfigureAwait(false);

        // An unreachable service says nothing about whether the user is signed in.
        if (WinoAccountClientErrorCodes.IsServiceFailure(errorCode))
            throw new WinoAccountApiException(errorCode!);

        return account;
    }

    /// <summary>
    /// Resolves the signed-in account with a usable access token, refreshing it when expired.
    /// Returns the failure code instead of the account when that is not possible.
    /// </summary>
    private async Task<(WinoAccount? Account, string? ErrorCode)> AuthenticateAsync(CancellationToken cancellationToken)
    {
        var account = await GetActiveAccountAsync().ConfigureAwait(false);

        if (account == null)
        {
            return (null, WinoAccountClientErrorCodes.SignInRequired);
        }

        if (string.IsNullOrWhiteSpace(account.AccessToken))
        {
            _logger.Warning("Wino account {Email} is missing an access token.", account.Email);
            return (null, WinoAccountClientErrorCodes.SignInRequired);
        }

        if (account.AccessTokenExpiresAtUtc > DateTime.UtcNow)
        {
            return (account, null);
        }

        var refreshResult = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (!refreshResult.IsSuccess)
        {
            _logger.Warning("Wino account access token refresh failed with error code {ErrorCode}.", refreshResult.ErrorCode);
            return (null, refreshResult.ErrorCode ?? ApiErrorCodes.RefreshTokenInvalid);
        }

        var refreshed = refreshResult.Account ?? await GetActiveAccountAsync().ConfigureAwait(false);
        return refreshed is null
            ? (null, WinoAccountClientErrorCodes.SignInRequired)
            : (refreshed, null);
    }

    private async Task RequireAuthenticatedAccountAsync(CancellationToken cancellationToken)
    {
        var (account, errorCode) = await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
        if (account is null)
            throw new WinoAccountApiException(errorCode ?? WinoAccountClientErrorCodes.SignInRequired);
    }

    public async Task<bool> HasActiveAccountAsync()
        => await Connection.Table<WinoAccount>().CountAsync().ConfigureAwait(false) > 0;

    private readonly SemaphoreSlim _profileLock = new(1, 1);

    public Task<ApiEnvelope<AuthUserDto>> GetCurrentUserAsync(CancellationToken cancellationToken = default)
        => ExecuteProfileRequestAsync(_apiClient.GetCurrentUserAsync, cancellationToken);

    public Task<ApiEnvelope<AuthUserDto>> UpdateProfileAsync(string? displayName, CancellationToken cancellationToken = default)
        => ExecuteProfileRequestAsync(token => _apiClient.UpdateProfileAsync(displayName, token), cancellationToken);

    public Task<ApiEnvelope<AuthUserDto>> UploadAvatarAsync(byte[] payload, CancellationToken cancellationToken = default)
        => ExecuteProfileRequestAsync(token => _apiClient.UploadAvatarAsync(payload, token), cancellationToken);

    public Task<ApiEnvelope<AuthUserDto>> DeleteAvatarAsync(CancellationToken cancellationToken = default)
        => ExecuteProfileRequestAsync(_apiClient.DeleteAvatarAsync, cancellationToken);

    private async Task<ApiEnvelope<AuthUserDto>> ExecuteProfileRequestAsync(Func<CancellationToken, Task<ApiEnvelope<AuthUserDto>>> request, CancellationToken cancellationToken)
    {
        var queuedSession = await _sessions.CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (queuedSession is null) return ApiEnvelope<AuthUserDto>.Failure(WinoAccountClientErrorCodes.SignInRequired);
        await _profileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await _sessions.IsCurrentAsync(queuedSession, cancellationToken).ConfigureAwait(false))
                return ApiEnvelope<AuthUserDto>.Failure(WinoAccountClientErrorCodes.AccountSessionChanged);
            return await ExecuteProfileRequestCoreAsync(queuedSession, request, cancellationToken).ConfigureAwait(false);
        }
        finally { _profileLock.Release(); }
    }

    private async Task<ApiEnvelope<AuthUserDto>> ExecuteProfileRequestCoreAsync(WinoAccountSession session, Func<CancellationToken, Task<ApiEnvelope<AuthUserDto>>> request, CancellationToken cancellationToken)
    {

        var (account, errorCode) = await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
        if (account is null)
            return ApiEnvelope<AuthUserDto>.Failure(errorCode ?? WinoAccountClientErrorCodes.SignInRequired);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.CancellationToken);
        var response = await request(linked.Token).ConfigureAwait(false);
        if (!response.IsSuccess || response.Result is null) return response;
        if (response.Result.UserId != session.AccountId) return ApiEnvelope<AuthUserDto>.Failure(WinoAccountClientErrorCodes.AccountSessionChanged);

        var committed = await _sessions.CommitAsync(session, async () =>
        {
            var current = await GetActiveAccountAsync().ConfigureAwait(false);
            var refreshed = MergeAccountProfile(current!, response.Result);
            if (!AreEquivalentProfiles(current!, refreshed))
            {
                await Connection.UpdateAsync(refreshed, typeof(WinoAccount)).ConfigureAwait(false);
                if (current!.AvatarRevision != refreshed.AvatarRevision) ClearAvatarCache(refreshed.Id, refreshed.AvatarRevision);
                PublishProfileUpdated(refreshed);
            }
        }, cancellationToken).ConfigureAwait(false);

        return committed ? response : ApiEnvelope<AuthUserDto>.Failure(WinoAccountClientErrorCodes.AccountSessionChanged);
    }

    public async Task<ApiEnvelope<AiSummaryResultDto>> SummarizeAsync(IReadOnlyList<MailContentSegment> segments, string targetLanguage, CancellationToken cancellationToken = default)
        => await ExecuteAiOperationAsync(account => _apiClient.SummarizeAsync(segments, targetLanguage, cancellationToken), "summarize", cancellationToken).ConfigureAwait(false);

    public async Task<ApiEnvelope<AiTranslationResultDto>> TranslateAsync(IReadOnlyList<MailContentSegment> segments, string? sourceLanguage, string targetLanguage, CancellationToken cancellationToken = default)
        => await ExecuteAiOperationAsync(account => _apiClient.TranslateAsync(segments, sourceLanguage, targetLanguage, cancellationToken), "translate", cancellationToken).ConfigureAwait(false);

    public async Task<ApiEnvelope<AiTextResultDto>> RewriteAsync(string html, string mode, string context, CancellationToken cancellationToken = default)
        => await ExecuteAiOperationAsync(
            account => _apiClient.RewriteAsync(
                html,
                mode,
                _translationService?.CurrentLanguageModel?.Code ?? CultureInfo.CurrentUICulture.Name ?? "en-US",
                context,
                cancellationToken),
            "rewrite",
            cancellationToken).ConfigureAwait(false);

    public async Task<WinoSyncSnapshotDownload?> GetSyncSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await RequireAuthenticatedAccountAsync(cancellationToken).ConfigureAwait(false);

        return await _apiClient.GetSyncSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserSyncSnapshotStatusDto> PutSyncSnapshotAsync(byte[] payload, long? expectedRevision = null, CancellationToken cancellationToken = default)
    {
        await RequireAuthenticatedAccountAsync(cancellationToken).ConfigureAwait(false);

        return await _apiClient.PutSyncSnapshotAsync(payload, expectedRevision, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserMailboxSyncListDto> GetMailboxesAsync(CancellationToken cancellationToken = default)
    {
        await RequireAuthenticatedAccountAsync(cancellationToken).ConfigureAwait(false);

        return await _apiClient.GetMailboxesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceMailboxesAsync(ReplaceUserMailboxesRequestDto request, CancellationToken cancellationToken = default)
    {
        await RequireAuthenticatedAccountAsync(cancellationToken).ConfigureAwait(false);

        await _apiClient.ReplaceMailboxesAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var account = await GetActiveAccountAsync().ConfigureAwait(false);

        // Account-owned local intelligence must be gone before local sign-out can succeed.
        await _sessions.ReplaceAsync(null, async () =>
        {
            ReportUIChange(new WinoIntelligenceEntitlementChanged(
                WinoIntelligenceEntitlementSnapshot.SignedOut(DateTimeOffset.UtcNow)));
            await PurgeLocalIntelligenceAsync(cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);

        if (account != null)
        {
            // Earlier builds cached a key derived from the account password. Backups now use their
            // own password, so nothing account-bound is left to keep.
            _snapshotKeys?.DeleteLegacyKeyCache();

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
            else if (existingAccount is not null && existingAccount.AvatarRevision != account.AvatarRevision)
                ClearAvatarCache(account.Id, account.AvatarRevision);
        }).ConfigureAwait(false);
    }

    private async Task PurgeLocalIntelligenceAsync(CancellationToken cancellationToken = default)
    {
        var account = await GetActiveAccountAsync().ConfigureAwait(false);
        if (account is not null)
        {
            _pendingCheckouts?.Clear(account.Id);
            ClearAvatarCache(account.Id);
        }

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

    private static string AvatarDirectory(Guid accountId)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wino", "AccountAvatars", accountId.ToString("N"));

    private void ClearAvatarCache(Guid accountId, Guid? keepRevision = null)
    {
        try
        {
            var directory = AvatarDirectory(accountId);
            if (!Directory.Exists(directory)) return;
            foreach (var file in Directory.EnumerateFiles(directory, "*.png"))
                if (Path.GetFileName(file) != $"{keepRevision:N}.png") File.Delete(file);
        }
        catch (IOException ex) { _logger.Warning(ex, "Account avatar cache could not be cleared."); }
        catch (UnauthorizedAccessException ex) { _logger.Warning(ex, "Account avatar cache could not be cleared."); }
    }

    public Task<byte[]> PrepareAvatarAsync(byte[] payload, CancellationToken cancellationToken = default)
        => Task.Run(() => WinoAccountAvatarProcessor.Normalize(payload), cancellationToken);

    public async Task<string?> GetAvatarPathAsync(Guid accountId, Guid? revision, CancellationToken cancellationToken = default)
    {
        if (revision is null) return null;
        var session = await _sessions.CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (session is null || session.AccountId != accountId) return null;
        var path = Path.Combine(AvatarDirectory(accountId), $"{revision:N}.png");
        try
        {
            if (!File.Exists(path))
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.CancellationToken);
                var avatar = await _apiClient.GetAvatarAsync(linked.Token).ConfigureAwait(false);
                if (avatar is null || avatar.Value.Revision != revision) return null;
                var saved = false;
                await _sessions.CommitAsync(session, async () =>
                {
                    var current = await GetActiveAccountAsync().ConfigureAwait(false);
                    if (current?.AvatarRevision != revision) return;
                    Directory.CreateDirectory(AvatarDirectory(accountId));
                    var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        await File.WriteAllBytesAsync(temporaryPath, avatar.Value.Payload, linked.Token).ConfigureAwait(false);
                        File.Move(temporaryPath, path, overwrite: true);
                        saved = true;
                    }
                    finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                }, linked.Token).ConfigureAwait(false);
                if (!saved) return null;
            }
            return await _sessions.IsCurrentAsync(session, cancellationToken).ConfigureAwait(false) ? path : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { _logger.Warning(ex, "Account avatar could not be loaded."); return null; }
    }

    private async Task<ApiEnvelope<T>> ExecuteAiOperationAsync<T>(Func<WinoAccount, Task<ApiEnvelope<T>>> executeAsync,
                                                                   string operationName,
                                                                   CancellationToken cancellationToken)
    {
        var (account, errorCode) = await AuthenticateAsync(cancellationToken).ConfigureAwait(false);
        if (account == null)
        {
            return ApiEnvelope<T>.Failure(errorCode ?? WinoAccountClientErrorCodes.SignInRequired);
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
           string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal) &&
           left.AvatarRevision == right.AvatarRevision &&
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
            DisplayName = profile.DisplayName ?? profile.Email,
            AvatarRevision = profile.AvatarRevision,
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
            DisplayName = result.User.DisplayName ?? result.User.Email,
            AvatarRevision = result.User.AvatarRevision,
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
