#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Api.Contracts.Common;

namespace Wino.Services;

public sealed class WinoStorePurchaseRedeemService(
    IDatabaseService databaseService,
    IWinoAccountApiClient apiClient,
    IMicrosoftStoreService storeService) : IWinoStorePurchaseRedeemService
{
    private readonly ILogger _logger = Log.ForContext<WinoStorePurchaseRedeemService>();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<WinoStorePurchaseRedeemOutcome> RedeemUnlimitedAccountsAsync(CancellationToken cancellationToken = default)
    {
        // Sign-in and Refresh purchases can overlap; the second caller sees the first one's result on the account.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var account = await databaseService.Connection.Table<WinoAccount>().FirstOrDefaultAsync().ConfigureAwait(false);
            if (account is null || account.IsUnlimitedAccountsEnabled)
                return WinoStorePurchaseRedeemOutcome.NotNeeded;

            if (!await storeService.HasProductAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS).ConfigureAwait(false))
                return WinoStorePurchaseRedeemOutcome.NotNeeded;

            var ticket = await apiClient.CreateStoreCollectionsIdTicketAsync(cancellationToken).ConfigureAwait(false);
            if (!ticket.IsSuccess || ticket.Result is null)
            {
                _logger.Warning("Store service ticket was not issued. Error code: {ErrorCode}", ticket.ErrorCode);
                return WinoStorePurchaseRedeemOutcome.Failed;
            }

            var storeIdKey = await storeService
                .GetCustomerCollectionsIdAsync(ticket.Result.ServiceTicket, ticket.Result.PublisherUserId)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(storeIdKey))
            {
                _logger.Warning("The Microsoft Store returned no Store ID key.");
                return WinoStorePurchaseRedeemOutcome.Failed;
            }

            var redeem = await apiClient.RedeemStoreUnlimitedAccountsAsync(storeIdKey, cancellationToken).ConfigureAwait(false);
            if (redeem.IsSuccess && redeem.Result?.IsUnlimitedAccountsEnabled == true)
            {
                _logger.Information("Microsoft Store Unlimited Accounts purchase was linked to the Wino Account.");
                return WinoStorePurchaseRedeemOutcome.Redeemed;
            }

            _logger.Information("Microsoft Store purchase was not redeemed. Error code: {ErrorCode}", redeem.ErrorCode);
            return redeem.ErrorCode switch
            {
                ApiErrorCodes.MicrosoftStorePurchaseAlreadyLinked => WinoStorePurchaseRedeemOutcome.AlreadyLinked,
                ApiErrorCodes.MicrosoftStorePurchaseNotFound => WinoStorePurchaseRedeemOutcome.NotOwned,
                _ => WinoStorePurchaseRedeemOutcome.Failed
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Microsoft Store purchase redeem failed.");
            return WinoStorePurchaseRedeemOutcome.Failed;
        }
        finally
        {
            _gate.Release();
        }
    }
}
