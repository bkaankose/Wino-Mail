#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Store;

namespace Wino.Services;

/// <summary>
/// Mac App Store purchases and their Wino Account link. See <see cref="IWinoAppStorePurchaseService"/>.
/// The App Store entitlement is the local source of truth for Unlimited Accounts; the Wino Account
/// API decides what a purchase grants the account. A transaction made for the signed-in account is
/// finished only after the API has linked it, so StoreKit delivers it again when the link fails.
/// </summary>
public sealed class WinoAppStorePurchaseService(
    IDatabaseService databaseService,
    IWinoAccountApiClient apiClient,
    IConfigurationService configuration,
    IPlatformCapabilities capabilities,
    IAppStoreClient? appStore = null) : IWinoAppStorePurchaseService
{
    private const string HiddenRedeemKeyPrefix = "AppStoreUnlimitedRedeemHidden_";
    private const string UnitedStatesStorefront = "USA";

    /// <summary>The API accepts at most this many signed transactions per request.</summary>
    private const int MaxTransactionsPerRequest = 20;

    private readonly ILogger _logger = Log.ForContext<WinoAppStorePurchaseService>();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _updatesStarted;

    public bool IsAvailable => capabilities.AppleAppStore && appStore is not null;

    public async Task<bool> HasUnlimitedAccountsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || appStore is null) return false;
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var entitlements = await appStore.GetCurrentEntitlementsAsync().ConfigureAwait(false);
            return entitlements.Any(t => !t.IsRevoked && t.ProductId == AppStoreProductIds.UnlimitedAccounts);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "App Store entitlements could not be read.");
            return false;
        }
    }

    public async Task<bool> IsExternalPurchaseAllowedAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || appStore is null) return true;
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return await appStore.GetStorefrontCountryCodeAsync().ConfigureAwait(false) == UnitedStatesStorefront;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "App Store storefront could not be read.");
            return false;
        }
    }

    public async Task<WinoAppStorePurchaseOutcome> PurchaseAsync(WinoAddOnProductType productType, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || appStore is null) return WinoAppStorePurchaseOutcome.Failed;
        cancellationToken.ThrowIfCancellationRequested();

        var account = await GetAccountAsync().ConfigureAwait(false);
        if (productType == WinoAddOnProductType.AI_PACK && account is null)
            return WinoAppStorePurchaseOutcome.SignInRequired;

        AppStorePurchase purchase;
        try
        {
            purchase = await appStore.PurchaseAsync(GetProductId(productType), account?.Id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "App Store purchase of {Product} failed.", productType);
            return WinoAppStorePurchaseOutcome.Failed;
        }

        if (purchase.Status == AppStorePurchaseStatus.Cancelled) return WinoAppStorePurchaseOutcome.Cancelled;
        if (purchase.Status == AppStorePurchaseStatus.Pending || purchase.Transaction is not { } transaction)
            return WinoAppStorePurchaseOutcome.Pending;

        // Bought signed out: the entitlement unlocks it here and the Redeem card offers the link later.
        if (account is null)
        {
            await FinishAsync(transaction).ConfigureAwait(false);
            return WinoAppStorePurchaseOutcome.Purchased;
        }

        return await LinkAndFinishAsync([transaction], cancellationToken).ConfigureAwait(false) is not null
            ? WinoAppStorePurchaseOutcome.Purchased
            : WinoAppStorePurchaseOutcome.PurchasedLinkPending;
    }

    public async Task<bool> SyncOwnedPurchasesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || appStore is null) return false;
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var account = await GetAccountAsync().ConfigureAwait(false);
            var unfinished = await appStore.GetUnfinishedTransactionsAsync().ConfigureAwait(false);

            // Transactions bought signed out need no link; finishing them only acknowledges delivery.
            foreach (var transaction in unfinished.Where(t => t.AppAccountToken is null))
                await FinishAsync(transaction).ConfigureAwait(false);

            if (account is null) return false;

            var entitlements = await appStore.GetCurrentEntitlementsAsync().ConfigureAwait(false);
            var owned = entitlements.Concat(unfinished)
                .Where(t => t.AppAccountToken == account.Id)
                .DistinctBy(t => t.Id)
                .ToList();
            if (owned.Count == 0) return false;

            return await LinkAndFinishAsync(owned, cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "App Store purchases could not be synchronized.");
            return false;
        }
    }

    public async Task<bool> RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || appStore is null) return false;
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await appStore.SyncAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The user may close the Apple Account sign-in; what StoreKit already has still syncs.
            _logger.Information(ex, "App Store sync did not complete.");
        }

        return await SyncOwnedPurchasesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasRedeemCandidateAsync(CancellationToken cancellationToken = default)
        => await GetRedeemCandidateAsync(cancellationToken).ConfigureAwait(false) is not null;

    public async Task<WinoStorePurchaseRedeemOutcome> RedeemAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return WinoStorePurchaseRedeemOutcome.Unavailable;

        var candidate = await GetRedeemCandidateAsync(cancellationToken).ConfigureAwait(false);
        if (candidate is null) return WinoStorePurchaseRedeemOutcome.NotNeeded;

        var result = await LinkAndFinishAsync([candidate], cancellationToken).ConfigureAwait(false);
        if (result is null) return WinoStorePurchaseRedeemOutcome.Failed;

        var outcome = result.Transactions
            .FirstOrDefault(t => t.OriginalTransactionId == candidate.OriginalId.ToString())?.Outcome;
        var redeemOutcome = outcome switch
        {
            AppStoreTransactionOutcomes.Linked => WinoStorePurchaseRedeemOutcome.Redeemed,
            AppStoreTransactionOutcomes.LinkedToAnotherAccount => WinoStorePurchaseRedeemOutcome.AlreadyLinked,
            AppStoreTransactionOutcomes.Revoked or AppStoreTransactionOutcomes.NotEligible => WinoStorePurchaseRedeemOutcome.NotOwned,
            _ => result.IsUnlimitedAccountsEnabled ? WinoStorePurchaseRedeemOutcome.Redeemed : WinoStorePurchaseRedeemOutcome.Failed
        };

        // Retrying cannot change these answers for this purchase, so stop offering the redeem.
        if (redeemOutcome is WinoStorePurchaseRedeemOutcome.AlreadyLinked or WinoStorePurchaseRedeemOutcome.NotOwned)
            configuration.Set(HiddenRedeemKeyPrefix + candidate.OriginalId, true);

        _logger.Information("App Store Unlimited Accounts redeem finished: {Outcome}.", redeemOutcome);
        return redeemOutcome;
    }

    public void StartTransactionUpdates()
    {
        if (!IsAvailable || appStore is null || Interlocked.Exchange(ref _updatesStarted, 1) == 1) return;

        appStore.TransactionUpdated += TransactionUpdated;
        appStore.StartTransactionUpdates();
    }

    private async void TransactionUpdated(object? sender, AppStoreTransactionInfo transaction)
    {
        try
        {
            var account = await GetAccountAsync().ConfigureAwait(false);
            if (account is not null && transaction.AppAccountToken == account.Id)
                await LinkAndFinishAsync([transaction], CancellationToken.None).ConfigureAwait(false);
            else if (transaction.AppAccountToken is null)
                await FinishAsync(transaction).ConfigureAwait(false);
            // A transaction for another Wino Account stays unfinished until that account signs in here.
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "App Store transaction update could not be handled.");
        }
    }

    private async Task<AppStoreTransactionInfo?> GetRedeemCandidateAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable || appStore is null) return null;
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var account = await GetAccountAsync().ConfigureAwait(false);
            if (account is null || account.IsUnlimitedAccountsEnabled) return null;

            // Only purchases made signed out: one made for any Wino Account is linked to that account.
            var entitlements = await appStore.GetCurrentEntitlementsAsync().ConfigureAwait(false);
            return entitlements.FirstOrDefault(t =>
                !t.IsRevoked &&
                t.ProductId == AppStoreProductIds.UnlimitedAccounts &&
                t.AppAccountToken is null &&
                !configuration.Get(HiddenRedeemKeyPrefix + t.OriginalId, false));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "App Store redeem candidate could not be read.");
            return null;
        }
    }

    /// <summary>
    /// Sends the transactions to the Wino Account API and finishes them once it answered. Returns null
    /// when the API did not answer, so the transactions stay unfinished and StoreKit delivers them again.
    /// </summary>
    private async Task<AppStoreRedeemResultDto?> LinkAndFinishAsync(IReadOnlyList<AppStoreTransactionInfo> transactions, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AppStoreRedeemResultDto? last = null;
            foreach (var chunk in transactions.Chunk(MaxTransactionsPerRequest))
            {
                var response = await apiClient
                    .RedeemAppStoreTransactionsAsync(chunk.Select(t => t.SignedTransaction).ToArray(), cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccess || response.Result is null)
                {
                    _logger.Warning("App Store transactions were not linked. Error code: {ErrorCode}", response.ErrorCode);
                    return null;
                }

                last = response.Result;
                foreach (var transaction in chunk)
                    await FinishAsync(transaction).ConfigureAwait(false);
            }

            return last;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "App Store transactions could not be linked.");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task FinishAsync(AppStoreTransactionInfo transaction)
    {
        if (appStore is null) return;

        try
        {
            await appStore.FinishAsync(transaction.Id).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // An unfinished transaction is delivered again and finished on a later sync.
            _logger.Warning(ex, "App Store transaction {TransactionId} could not be finished.", transaction.Id);
        }
    }

    private async Task<WinoAccount?> GetAccountAsync()
        => await databaseService.Connection.Table<WinoAccount>().FirstOrDefaultAsync().ConfigureAwait(false);

    private static string GetProductId(WinoAddOnProductType productType)
        => productType switch
        {
            WinoAddOnProductType.AI_PACK => AppStoreProductIds.AiPack,
            WinoAddOnProductType.UNLIMITED_ACCOUNTS => AppStoreProductIds.UnlimitedAccounts,
            _ => throw new ArgumentOutOfRangeException(nameof(productType), productType, null)
        };
}
