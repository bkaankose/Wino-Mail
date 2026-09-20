#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Contracts.Intelligence;
using Wino.Messaging.UI;

namespace Wino.Services;

public sealed class WinoAccountIntelligenceSnapshotService(
    IWinoBillingService billingService,
    IWinoAccountApiClient apiClient,
    IMailIntelligenceStore localStore,
    IWinoAccountSessionService sessions) : IWinoAccountIntelligenceSnapshotService
{
    private readonly ConcurrentDictionary<(Guid, long), Lazy<Task<WinoAccountIntelligenceRefreshResult?>>> _refreshes = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public async Task<WinoAccountIntelligenceSnapshot?> GetCachedAsync(Guid winoAccountId, CancellationToken cancellationToken = default)
    {
        var session = await sessions.CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (session?.AccountId != winoAccountId) return null;

        WinoAccountIntelligenceSnapshot? snapshot = null;
        await sessions.CommitAsync(session, async () =>
        {
            var payload = await localStore.GetAccountSnapshotJsonAsync(winoAccountId, cancellationToken).ConfigureAwait(false);
            snapshot = Deserialize(payload);
        }, cancellationToken).ConfigureAwait(false);
        return snapshot is null ? null : snapshot with { Session = session };
    }

    public async Task<WinoAccountIntelligenceRefreshResult?> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var session = await sessions.CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (session is null) return null;

        var key = (session.AccountId, session.Generation);
        var entry = _refreshes.GetOrAdd(key, _ => new(() => RefreshSerializedAsync(session, false), LazyThreadSafetyMode.ExecutionAndPublication));
        var task = entry.Value;
        _ = task.ContinueWith(completed =>
            _refreshes.TryRemove(new KeyValuePair<(Guid, long), Lazy<Task<WinoAccountIntelligenceRefreshResult?>>>(key, entry)),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<WinoAccountIntelligenceRefreshResult?> RefreshPurchasesAsync(CancellationToken cancellationToken = default)
    {
        var session = await sessions.CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (session is null) return null;

        return await RefreshSerializedAsync(session, true, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(WinoAccountIntelligenceSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        var session = snapshot.Session ?? await sessions.CaptureAsync(cancellationToken).ConfigureAwait(false);
        if (session?.AccountId != snapshot.WinoAccountId) return;

        await sessions.CommitAsync(session, () => SaveSnapshotAsync(snapshot, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
        => localStore.DeleteAccountSnapshotsAsync(cancellationToken);

    private Task SaveSnapshotAsync(WinoAccountIntelligenceSnapshot snapshot, CancellationToken cancellationToken)
        => localStore.SaveAccountSnapshotJsonAsync(
            snapshot.WinoAccountId,
            JsonSerializer.Serialize(snapshot, MailIntelligenceSnapshotJsonContext.Default.WinoAccountIntelligenceSnapshot),
            cancellationToken);

    private static WinoAccountIntelligenceSnapshot? Deserialize(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(payload, MailIntelligenceSnapshotJsonContext.Default.WinoAccountIntelligenceSnapshot);
        }
        catch (JsonException)
        {
            // A snapshot written by an older build is simply re-fetched.
            return null;
        }
    }

    private async Task<WinoAccountIntelligenceRefreshResult?> RefreshSerializedAsync(
        WinoAccountSession session, bool purchasesOnly, CancellationToken cancellationToken = default)
    {
        // Optional metadata must not hold an explicit purchase refresh behind the API's long timeout.
        using var backgroundTimeout = new CancellationTokenSource();
        if (!purchasesOnly) backgroundTimeout.CancelAfter(TimeSpan.FromSeconds(15));

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.CancellationToken, backgroundTimeout.Token);
        await _refreshLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            return await RefreshCoreAsync(session, purchasesOnly, linked.Token).ConfigureAwait(false);
        }
        finally { _refreshLock.Release(); }
    }

    private async Task<WinoAccountIntelligenceRefreshResult?> RefreshCoreAsync(WinoAccountSession session, bool purchasesOnly, CancellationToken cancellationToken)
    {
        var accountId = session.AccountId;
        var existing = await GetCachedAsync(accountId, cancellationToken).ConfigureAwait(false)
            ?? WinoAccountIntelligenceSnapshot.Empty(accountId);
        cancellationToken.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        var changed = false;
        var billingRefreshed = false;
        var billing = existing.Billing;
        var consent = existing.Consent;
        var usage = existing.Usage;
        DateTimeOffset? billingAt = existing.BillingUpdatedAtUtc, consentAt = existing.ConsentUpdatedAtUtc,
            usageAt = existing.UsageUpdatedAtUtc;

        var billingTask = billingService.GetStatusAsync(cancellationToken);
        try
        {
            var result = await billingTask.ConfigureAwait(false);
            if (result.IsSuccess && result.Result is not null) { billing = result.Result; billingAt = now; changed = true; billingRefreshed = true; }
            else errors.Add(result.ErrorCode ?? "Billing status could not be refreshed.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { errors.Add(ex.Message); }
        if (!purchasesOnly)
        {
            try
            {
                consent = await apiClient.GetIntelligenceConsentAsync(cancellationToken).ConfigureAwait(false);
                consentAt = now;
                changed = true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { errors.Add(ex.Message); }

            if (billing?.AiPack?.HasAccess == true)
            {
                try
                {
                    var result = await apiClient.GetAiUsageAsync(cancellationToken).ConfigureAwait(false);
                    if (result.IsSuccess && result.Result is not null)
                    {
                        usage = result.Result;
                        usageAt = now;
                        changed = true;
                    }
                    else errors.Add(result.ErrorCode ?? "Usage could not be refreshed.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex) { errors.Add(ex.Message); }

            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = new WinoAccountIntelligenceSnapshot(
            accountId, billing, consent, usage,
            billingAt, consentAt, usageAt,
            changed ? now : existing.LastSuccessfulRefreshUtc)
        {
            Session = session,
        };
        if (changed)
        {
            if (!await sessions.CommitAsync(session, async () =>
            {
                await SaveSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
                if (billingRefreshed)
                {
                    WeakReferenceMessenger.Default.Send(new WinoIntelligenceEntitlementChanged(
                        WinoIntelligenceEntitlementSnapshot.Evaluate(
                            accountId,
                            snapshot.Billing,
                            snapshot.Usage,
                            now,
                            isFreshBilling: true)));
                }
                WeakReferenceMessenger.Default.Send(new WinoIntelligenceAccessChanged());
            }, cancellationToken).ConfigureAwait(false)) return null;
        }
        return new(snapshot, changed, errors.FirstOrDefault()) { BillingRefreshed = billingRefreshed };
    }
}

[JsonSerializable(typeof(WinoAccountIntelligenceSnapshot))]
internal sealed partial class MailIntelligenceSnapshotJsonContext : JsonSerializerContext;
