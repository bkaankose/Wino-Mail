#nullable enable
using System;
using System.Reflection;
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
using Wino.Mail.Api.Contracts.Billing;
using Wino.Mail.Api.Contracts.Common;
using Wino.Messaging.UI;

namespace Wino.Services;

public sealed class WinoAccountProfileService : BaseDatabaseService, IWinoAccountProfileService
{
    private readonly IWinoAccountApiClient _apiClient;
    private readonly IStoreManagementService _storeManagementService;
    private readonly SemaphoreSlim _billingCallbackLock = new(1, 1);
    private readonly ILogger _logger = Log.ForContext<WinoAccountProfileService>();

    public WinoAccountProfileService(IDatabaseService databaseService,
                                     IWinoAccountApiClient apiClient,
                                     IStoreManagementService storeManagementService) : base(databaseService)
    {
        _apiClient = apiClient;
        _storeManagementService = storeManagementService;
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
            return result;
        }

        if (result.Account != null && !AreEquivalentProfiles(account, result.Account))
        {
            PublishProfileUpdated(result.Account);
        }

        return result;
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

    public async Task<bool> HasAddOnAsync(WinoAddOnProductType productId, CancellationToken cancellationToken = default)
    {
        return productId switch
        {
            WinoAddOnProductType.AI_PACK => await HasAiPackAsync(cancellationToken).ConfigureAwait(false),
            WinoAddOnProductType.UNLIMITED_ACCOUNTS => await HasUnlimitedAccountsAsync(cancellationToken).ConfigureAwait(false),
            _ => false
        };
    }

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

    public async Task<ApiEnvelope<CheckoutSessionResultDto>> CreateCheckoutSessionAsync(WinoAddOnProductType productId, CancellationToken cancellationToken = default)
    {
        var account = await GetAuthenticatedAccountAsync(cancellationToken).ConfigureAwait(false);
        if (account == null)
        {
            return ApiEnvelope<CheckoutSessionResultDto>.Failure("MissingAccessToken");
        }

        var response = await _apiClient.CreateCheckoutSessionAsync(productId, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            _logger.Warning("Failed to create checkout session for product {ProductId} and Wino account {Email}. Error code: {ErrorCode}", productId, account.Email, response.ErrorCode);
        }

        return response;
    }

    public async Task<ApiEnvelope<CustomerPortalResultDto>> CreateCustomerPortalSessionAsync(CancellationToken cancellationToken = default)
    {
        var account = await GetAuthenticatedAccountAsync(cancellationToken).ConfigureAwait(false);
        if (account == null)
        {
            return ApiEnvelope<CustomerPortalResultDto>.Failure("MissingAccessToken");
        }

        var response = await _apiClient.CreateCustomerPortalSessionAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            _logger.Warning("Failed to create customer portal session for Wino account {Email}. Error code: {ErrorCode}", account.Email, response.ErrorCode);
        }

        return response;
    }

    public async Task<bool> ProcessBillingCallbackAsync(Uri callbackUri, CancellationToken cancellationToken = default)
    {
        await _billingCallbackLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var targetProductType = ResolveProductType(callbackUri);
            if (targetProductType == null)
            {
                _logger.Warning("Billing callback was ignored because productCode is missing or unsupported. Uri: {Uri}", callbackUri);
                return false;
            }

            if (await GetAuthenticatedAccountAsync(cancellationToken).ConfigureAwait(false) == null)
            {
                _logger.Warning("Billing callback was ignored because there is no authenticated Wino account.");
                return false;
            }

            const int maxAttempts = 15;

            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var refreshResult = await RefreshProfileAsync(cancellationToken).ConfigureAwait(false);

                if (refreshResult.IsSuccess && await HasAddOnAsync(targetProductType.Value, cancellationToken).ConfigureAwait(false))
                {
                    ReportUIChange(new WinoAccountAddOnPurchasedMessage(targetProductType.Value));
                    return true;
                }

                if (attempt < maxAttempts - 1)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
            }

            return false;
        }
        finally
        {
            _billingCallbackLock.Release();
        }
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

    private void PublishProfileUpdated(WinoAccount account)
        => ReportUIChange(new WinoAccountProfileUpdatedMessage(account));

    private async Task<bool> HasAiPackAsync(CancellationToken cancellationToken)
    {
        var response = await GetAiStatusAsync(cancellationToken).ConfigureAwait(false);
        return response.IsSuccess && response.Result?.HasAiPack == true;
    }

    private async Task<bool> HasUnlimitedAccountsAsync(CancellationToken cancellationToken)
    {
        if (await HasRemoteUnlimitedAccountsAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        return await _storeManagementService.HasProductAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS).ConfigureAwait(false);
    }

    private async Task<bool> HasRemoteUnlimitedAccountsAsync(CancellationToken cancellationToken)
    {
        var response = await GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess || response.Result == null)
        {
            return false;
        }

        return TryGetBooleanProperty(response.Result, "HasUnlimitedAccounts", out var hasUnlimitedAccounts) && hasUnlimitedAccounts;
    }

    private static bool AreEquivalentProfiles(WinoAccount left, WinoAccount right)
        => left.Id == right.Id &&
           string.Equals(left.Email, right.Email, StringComparison.Ordinal) &&
           string.Equals(left.AccountStatus, right.AccountStatus, StringComparison.Ordinal) &&
           left.HasPassword == right.HasPassword &&
           left.HasGoogleLogin == right.HasGoogleLogin &&
           left.HasFacebookLogin == right.HasFacebookLogin;

    private static WinoAccount MergeAccountProfile(WinoAccount existingAccount, AuthUserDto profile)
        => new()
        {
            Id = profile.UserId,
            Email = profile.Email,
            AccountStatus = profile.AccountStatus,
            HasPassword = profile.HasPassword,
            HasGoogleLogin = profile.HasGoogleLogin,
            HasFacebookLogin = profile.HasFacebookLogin,
            AccessToken = existingAccount.AccessToken,
            AccessTokenExpiresAtUtc = existingAccount.AccessTokenExpiresAtUtc,
            RefreshToken = existingAccount.RefreshToken,
            RefreshTokenExpiresAtUtc = existingAccount.RefreshTokenExpiresAtUtc,
            LastAuthenticatedUtc = existingAccount.LastAuthenticatedUtc
        };

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The reflected contract property is a stable API field read from a concrete DTO instance.")]
    private static bool TryGetBooleanProperty(object instance, string propertyName, out bool value)
    {
        value = false;

        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        if (property?.PropertyType != typeof(bool))
        {
            return false;
        }

        value = (bool)(property.GetValue(instance) ?? false);
        return true;
    }

    private static WinoAddOnProductType? ResolveProductType(Uri callbackUri)
    {
        var productCode = GetQueryParameter(callbackUri, "productCode");
        if (string.IsNullOrWhiteSpace(productCode))
        {
            return null;
        }

        return productCode.Trim().ToUpperInvariant() switch
        {
            "AI_PACK" => WinoAddOnProductType.AI_PACK,
            "UNLIMITED_ACCOUNTS" => WinoAddOnProductType.UNLIMITED_ACCOUNTS,
            _ => null
        };
    }

    private static string GetQueryParameter(Uri uri, string key)
    {
        var query = uri.Query;
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 0 || !string.Equals(pieces[0], key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return pieces.Length > 1 ? Uri.UnescapeDataString(pieces[1]) : string.Empty;
        }

        return string.Empty;
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
