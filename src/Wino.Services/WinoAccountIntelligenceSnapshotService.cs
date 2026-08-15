#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Contracts.Intelligence;
using Wino.Messaging.UI;

namespace Wino.Services;

public sealed class WinoAccountIntelligenceSnapshotService(
    IWinoAccountProfileService profileService,
    IWinoBillingService billingService,
    IWinoAccountApiClient apiClient,
    ILocalIntelligenceStore localStore) : IWinoAccountIntelligenceSnapshotService
{
    private readonly ConcurrentDictionary<Guid, Task<WinoAccountIntelligenceRefreshResult?>> _refreshes = new();

    public Task<WinoAccountIntelligenceSnapshot?> GetCachedAsync(Guid winoAccountId, CancellationToken cancellationToken = default)
        => localStore.GetAccountIntelligenceSnapshotAsync(winoAccountId, cancellationToken);

    public async Task<WinoAccountIntelligenceRefreshResult?> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var account = await profileService.GetActiveAccountAsync().ConfigureAwait(false);
        if (account is null) return null;
        var task = _refreshes.GetOrAdd(account.Id, _ => RefreshCoreAsync(account.Id));
        try { return await task.WaitAsync(cancellationToken).ConfigureAwait(false); }
        finally { if (task.IsCompleted) _refreshes.TryRemove(account.Id, out _); }
    }

    public Task SaveAsync(WinoAccountIntelligenceSnapshot snapshot, CancellationToken cancellationToken = default)
        => localStore.SaveAccountIntelligenceSnapshotAsync(snapshot, cancellationToken);

    public Task ClearAsync(CancellationToken cancellationToken = default)
        => localStore.DeleteAccountIntelligenceSnapshotsAsync(cancellationToken);

    private async Task<WinoAccountIntelligenceRefreshResult?> RefreshCoreAsync(Guid accountId)
    {
        var existing = await localStore.GetAccountIntelligenceSnapshotAsync(accountId).ConfigureAwait(false)
            ?? WinoAccountIntelligenceSnapshot.Empty(accountId);
        var now = DateTimeOffset.UtcNow;
        var errors = new List<string>();
        var changed = false;
        var billing = existing.Billing;
        var consent = existing.Consent;
        var usage = existing.Usage;
        var mailboxes = existing.Mailboxes;
        var statuses = existing.MailboxStatuses;
        DateTimeOffset? billingAt = existing.BillingUpdatedAtUtc, consentAt = existing.ConsentUpdatedAtUtc,
            usageAt = existing.UsageUpdatedAtUtc, mailboxesAt = existing.MailboxesUpdatedAtUtc, statusesAt = existing.StatusesUpdatedAtUtc;

        var billingTask = billingService.GetStatusAsync();
        var consentTask = apiClient.GetIntelligenceConsentAsync();
        var usageTask = apiClient.GetAiUsageAsync();
        var mailboxesTask = apiClient.GetSemanticMailboxesAsync();
        try
        {
            var result = await billingTask.ConfigureAwait(false);
            if (result.IsSuccess && result.Result is not null) { billing = result.Result; billingAt = now; changed = true; }
            else errors.Add(result.ErrorCode ?? "Billing status could not be refreshed.");
        }
        catch (Exception ex) { errors.Add(ex.Message); }
        try { consent = await consentTask.ConfigureAwait(false); consentAt = now; changed = true; }
        catch (Exception ex) { errors.Add(ex.Message); }
        try
        {
            var result = await usageTask.ConfigureAwait(false);
            if (result.IsSuccess && result.Result is not null) { usage = result.Result; usageAt = now; changed = true; }
            else errors.Add(result.ErrorCode ?? "Usage could not be refreshed.");
        }
        catch (Exception ex) { errors.Add(ex.Message); }
        try
        {
            mailboxes = await mailboxesTask.ConfigureAwait(false);
            mailboxesAt = now;
            changed = true;
            var responses = await Task.WhenAll(mailboxes.Select(async mailbox =>
            {
                try { return (mailbox.MailboxId, Status: await apiClient.GetIntelligenceStatusAsync(mailbox.MailboxId).ConfigureAwait(false), Error: (string?)null); }
                catch (Exception ex) { return (mailbox.MailboxId, Status: (IntelligenceMailboxStatusDto?)null, Error: ex.Message); }
            })).ConfigureAwait(false);
            var nextStatuses = new Dictionary<Guid, IntelligenceMailboxStatusDto>(statuses);
            foreach (var response in responses)
            {
                if (response.Status is not null) { nextStatuses[response.MailboxId] = response.Status; changed = true; }
                else if (!string.IsNullOrWhiteSpace(response.Error)) errors.Add(response.Error);
            }
            statuses = nextStatuses;
            if (responses.Any(x => x.Status is not null)) statusesAt = now;
        }
        catch (Exception ex) { errors.Add(ex.Message); }

        var snapshot = new WinoAccountIntelligenceSnapshot(accountId, billing, consent, usage, mailboxes, statuses,
            billingAt, consentAt, usageAt, mailboxesAt, statusesAt, changed ? now : existing.LastSuccessfulRefreshUtc);
        if (changed)
        {
            await localStore.SaveAccountIntelligenceSnapshotAsync(snapshot).ConfigureAwait(false);
            WeakReferenceMessenger.Default.Send(new WinoIntelligenceAccessChanged());
        }
        return new(snapshot, changed, errors.FirstOrDefault());
    }
}
