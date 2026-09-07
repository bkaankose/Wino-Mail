#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Api.Contracts.Billing;
using Wino.Mail.Api.Contracts.Common;

namespace Wino.Services;

public sealed class WinoBillingService(
    IDatabaseService databaseService,
    IWinoAccountApiClient apiClient,
    IStoreManagementService storeManagementService,
    INativeAppService nativeAppService,
    IWinoPendingCheckoutStore? pendingCheckouts = null,
    IWinoAccountSessionService? sessions = null) : IWinoBillingService
{
    public Task<ApiEnvelope<CheckoutSessionResultDto>> CreateCheckoutSessionAsync(
        WinoAddOnProductType productType,
        CancellationToken cancellationToken = default)
        => apiClient.CreateCheckoutSessionAsync(GetProductCode(productType), cancellationToken);

    public async Task<bool> OpenCheckoutAsync(WinoAddOnProductType productType, CancellationToken cancellationToken = default)
    {
        var session = sessions is null ? null : await sessions.CaptureAsync(cancellationToken).ConfigureAwait(false);
        var response = await CreateCheckoutSessionAsync(productType, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess || response.Result is null ||
            !Uri.TryCreate(response.Result.Url, UriKind.Absolute, out var checkoutUri) ||
            checkoutUri.Scheme != Uri.UriSchemeHttps)
            return false;

        if (sessions is not null && (session is null || !await sessions.CommitAsync(session, () =>
        {
            pendingCheckouts?.Save(session.AccountId, productType);
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false))) return false;

        var launched = false;
        try
        {
            launched = await nativeAppService.LaunchUriAsync(checkoutUri).ConfigureAwait(false);
            return launched;
        }
        finally
        {
            if (!launched && session is not null)
                pendingCheckouts?.Clear(session.AccountId);
        }
    }

    public Task<ApiEnvelope<BillingStatusResultDto>> GetStatusAsync(CancellationToken cancellationToken = default)
        => apiClient.GetBillingStatusAsync(cancellationToken);

    public async Task<bool> HasUnlimitedAccountsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var account = await databaseService.Connection.Table<WinoAccount>().FirstOrDefaultAsync().ConfigureAwait(false);
        if (account?.IsUnlimitedAccountsEnabled == true)
        {
            return true;
        }

        return await storeManagementService.HasProductAsync(WinoAddOnProductType.UNLIMITED_ACCOUNTS).ConfigureAwait(false);
    }

    private static string GetProductCode(WinoAddOnProductType productType)
        => productType switch
        {
            WinoAddOnProductType.AI_PACK => "AI_PACK",
            WinoAddOnProductType.UNLIMITED_ACCOUNTS => "UNLIMITED_ACCOUNTS",
            _ => throw new ArgumentOutOfRangeException(nameof(productType), productType, null)
        };
}
