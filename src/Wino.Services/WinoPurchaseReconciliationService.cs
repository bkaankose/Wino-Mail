#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Mail.Api.Contracts.Common;

namespace Wino.Services;

public sealed class WinoPurchaseReconciliationService(
    IWinoAccountProfileService profileService,
    IWinoAccountIntelligenceSnapshotService snapshots,
    IWinoAccountSessionService sessions,
    IWinoPendingCheckoutStore pendingCheckouts,
    IWinoLogger logger,
    TimeProvider? timeProvider = null) : IWinoPurchaseReconciliationService
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(Guid, long, bool), Lazy<Task<WinoPurchaseRefreshResult>>> _refreshes = new();

    public async Task<WinoPurchaseRefreshResult> RefreshAsync(bool checkoutCompleted = false, CancellationToken cancellationToken = default)
    {
        var session = await sessions.CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (session is null) return new(WinoPurchaseRefreshOutcome.SignInRequired);

        var key = (session.AccountId, session.Generation, checkoutCompleted);
        var entry = _refreshes.GetOrAdd(key, _ => new(() => ReconcileAsync(session, checkoutCompleted), LazyThreadSafetyMode.ExecutionAndPublication));
        var task = entry.Value;
        _ = task.ContinueWith(completed =>
            _refreshes.TryRemove(new KeyValuePair<(Guid, long, bool), Lazy<Task<WinoPurchaseRefreshResult>>>(key, entry)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<WinoPurchaseRefreshResult> ReconcileAsync(WinoAccountSession session, bool checkoutCompleted)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(session.CancellationToken, timeout.Token);
        var cancellationToken = linked.Token;
        var expectedProduct = pendingCheckouts.Get(session.AccountId);
        WinoPurchaseRefreshResult? latest = null;
        var delaySeconds = 1;

        try
        {
            do
            {
                var profile = await profileService.RefreshProfileAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!profile.IsSuccess || profile.Account is null)
                    return new(profile.ErrorCode is ApiErrorCodes.RefreshTokenInvalid or "MissingAccessToken" or "AccountSessionChanged"
                        ? WinoPurchaseRefreshOutcome.SignInRequired : WinoPurchaseRefreshOutcome.Failed);

                var refresh = await snapshots.RefreshPurchasesAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (refresh?.BillingRefreshed != true || refresh.Snapshot.Billing is null)
                    return new(WinoPurchaseRefreshOutcome.Failed, profile.Account, refresh?.Snapshot);

                if (!await sessions.IsCurrentAsync(session, cancellationToken).ConfigureAwait(false))
                    return new(WinoPurchaseRefreshOutcome.SignInRequired);

                var billing = refresh.Snapshot.Billing;
                var confirmed = expectedProduct switch
                {
                    WinoAddOnProductType.AI_PACK => billing.AiPack?.HasAccess == true,
                    WinoAddOnProductType.UNLIMITED_ACCOUNTS => billing.IsUnlimitedAccountsEnabled && profile.Account.IsUnlimitedAccountsEnabled,
                    _ => true
                };
                latest = new(confirmed ? WinoPurchaseRefreshOutcome.Refreshed : WinoPurchaseRefreshOutcome.Pending,
                    profile.Account, refresh.Snapshot);

                if (confirmed)
                {
                    if (!await sessions.CommitAsync(session, () =>
                    {
                        pendingCheckouts.Clear(session.AccountId);
                        return Task.CompletedTask;
                    }, cancellationToken).ConfigureAwait(false))
                        return new(WinoPurchaseRefreshOutcome.SignInRequired);
                    return latest;
                }

                if (!checkoutCompleted) return latest;

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), _time, cancellationToken).ConfigureAwait(false);
                delaySeconds = Math.Min(delaySeconds * 2, 5);
            } while (true);
        }
        catch (OperationCanceledException) when (session.CancellationToken.IsCancellationRequested)
        {
            return new(WinoPurchaseRefreshOutcome.SignInRequired);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            return latest ?? new(WinoPurchaseRefreshOutcome.Failed);
        }
        catch (Exception exception)
        {
            logger.CaptureException(exception, nameof(WinoPurchaseReconciliationService));
            return new(WinoPurchaseRefreshOutcome.Failed);
        }
    }
}
