#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Ai;
using Wino.Mail.Api.Contracts.Auth;
using Wino.Mail.Api.Contracts.Common;
using Wino.Messaging.UI;

namespace Wino.Services;

public sealed class WinoAccountProfileService : BaseDatabaseService, IWinoAccountProfileService
{
    private readonly IWinoAccountApiClient _apiClient;
    private readonly ILogger _logger = Log.ForContext<WinoAccountProfileService>();

    public WinoAccountProfileService(IDatabaseService databaseService, IWinoAccountApiClient apiClient) : base(databaseService)
    {
        _apiClient = apiClient;
    }

    public async Task<WinoAccountOperationResult> RegisterAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        var response = await _apiClient.RegisterAsync(email, password, cancellationToken).ConfigureAwait(false);
        var result = await PersistResponseAsync(response).ConfigureAwait(false);

        if (result.IsSuccess && result.Account != null)
        {
            ReportUIChange(new WinoAccountSignedInMessage(result.Account));
        }

        return result;
    }

    public async Task<WinoAccountOperationResult> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        var response = await _apiClient.LoginAsync(email, password, cancellationToken).ConfigureAwait(false);
        var result = await PersistResponseAsync(response).ConfigureAwait(false);

        if (result.IsSuccess && result.Account != null)
        {
            ReportUIChange(new WinoAccountSignedInMessage(result.Account));
        }

        return result;
    }

    public async Task<WinoAccountOperationResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var account = await GetActiveAccountAsync().ConfigureAwait(false);
        if (account == null || string.IsNullOrWhiteSpace(account.RefreshToken))
        {
            _logger.Warning("Wino account token refresh skipped because there is no active account or refresh token.");
            return WinoAccountOperationResult.Failure(ApiErrorCodes.RefreshTokenInvalid);
        }

        _logger.Information("Refreshing Wino account token for {Email}", account.Email);
        var response = await _apiClient.RefreshAsync(account.RefreshToken, cancellationToken).ConfigureAwait(false);
        var result = await PersistResponseAsync(response).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            _logger.Warning("Wino account token refresh failed for {Email}. Error code: {ErrorCode}", account.Email, result.ErrorCode);
        }

        return result;
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

        if (account.AccessTokenExpiresAtUtc > DateTime.UtcNow.AddMinutes(1))
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
        }

        return response;
    }

    public async Task<ApiEnvelope<AiStatusResultDto>> GetAiStatusAsync(CancellationToken cancellationToken = default)
    {
        var account = await GetAuthenticatedAccountAsync(cancellationToken).ConfigureAwait(false);
        if (account == null)
        {
            return ApiEnvelope<AiStatusResultDto>.Failure("MissingAccessToken");
        }

        var response = await _apiClient.GetAiStatusAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            _logger.Warning("Failed to load AI status for Wino account {Email}. Error code: {ErrorCode}", account.Email, response.ErrorCode);
        }

        return response;
    }

    public async Task<ApiEnvelope<string>> CreateCheckoutSessionAsync(string productId, CancellationToken cancellationToken = default)
    {
        var account = await GetAuthenticatedAccountAsync(cancellationToken).ConfigureAwait(false);
        if (account == null)
        {
            return ApiEnvelope<string>.Failure("MissingAccessToken");
        }

        var response = await _apiClient.CreateCheckoutSessionAsync(productId, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            _logger.Warning("Failed to create checkout session for product {ProductId} and Wino account {Email}. Error code: {ErrorCode}", productId, account.Email, response.ErrorCode);
        }

        return response;
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
            ReportUIChange(new WinoAccountSignedOutMessage(account));
        }
    }

    private async Task<WinoAccountOperationResult> PersistResponseAsync(ApiEnvelope<AuthResultDto> response)
    {
        if (!response.IsSuccess || response.Result == null)
        {
            _logger.Warning("Wino account operation failed. Error code: {ErrorCode}", response.ErrorCode);
            return WinoAccountOperationResult.Failure(response.ErrorCode);
        }

        var account = Map(response.Result);

        await Connection.DeleteAllAsync<WinoAccount>().ConfigureAwait(false);
        await Connection.InsertOrReplaceAsync(account, typeof(WinoAccount)).ConfigureAwait(false);

        return WinoAccountOperationResult.Success(account);
    }

    private static WinoAccount Map(AuthResultDto result)
        => new()
        {
            Id = result.User.UserId,
            Email = result.User.Email,
            AccountStatus = result.User.AccountStatus,
            HasPassword = result.User.HasPassword,
            HasGoogleLogin = result.User.HasGoogleLogin,
            HasFacebookLogin = result.User.HasFacebookLogin,
            AccessToken = result.AccessToken,
            AccessTokenExpiresAtUtc = result.AccessTokenExpiresAtUtc.UtcDateTime,
            RefreshToken = result.RefreshToken,
            RefreshTokenExpiresAtUtc = result.RefreshTokenExpiresAtUtc.UtcDateTime,
            LastAuthenticatedUtc = DateTime.UtcNow
        };
}
