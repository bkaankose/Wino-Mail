#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Messaging.UI;

namespace Wino.Services;

/// <summary>
/// Builds the daily briefing from locally imported artifacts.
/// Only messages Classification included appear, grouped by the day they were received. Nothing here
/// infers a due date, an action or a status: those were the least reliable parts of the
/// previous design and the decision model cannot produce them.
/// </summary>
public sealed class LocalIntelligenceService : ILocalIntelligenceService,
    IRecipient<IntelligenceMetadataChanged>,
    IRecipient<IntelligenceVisibilityChanged>,
    IDisposable
{
    /// <summary>
    /// How far back the briefing looks. Cards are grouped by received day, so there is no
    /// forward window any more.
    /// </summary>
    private const int LookbackDays = 30;

    private readonly IDatabaseService _databaseService;
    private readonly IMailIntelligenceStore _store;
    private readonly IAccountService _accountService;

    public LocalIntelligenceService(
        IDatabaseService databaseService,
        IMailIntelligenceStore store,
        IAccountService accountService)
    {
        _databaseService = databaseService;
        _store = store;
        _accountService = accountService;
        WeakReferenceMessenger.Default.Register<LocalIntelligenceService, IntelligenceMetadataChanged>(
            this, static (recipient, message) => recipient.Receive(message));
        WeakReferenceMessenger.Default.Register<LocalIntelligenceService, IntelligenceVisibilityChanged>(
            this, static (recipient, message) => recipient.Receive(message));
    }

    public async Task<IReadOnlyList<DailyBriefingAccount>> GetEligibleAccountsAsync(CancellationToken cancellationToken = default)
    {
        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
        return accounts
            .Where(static x => x.IsMailAccessGranted && x.Preferences?.IsDailyBriefingEnabled != false)
            .OrderBy(static x => x.Order)
            .Select(static account => new DailyBriefingAccount(account))
            .ToArray();
    }

    public async Task<DailyBriefingFactsResult> GetBriefingFactsAsync(
        TimeZoneInfo timeZone,
        bool includeIgnored = false,
        CancellationToken cancellationToken = default)
    {
        var eligible = await GetEligibleAccountsAsync(cancellationToken).ConfigureAwait(false);
        if (eligible.Count == 0)
        {
            return DailyBriefingFactsResult.Empty;
        }

        await _databaseService.InitializeAsync().ConfigureAwait(false);

        // sqlite-net turns method calls inside the expression tree into SQL functions and
        // SQLite has no AddDays, so the bound is computed before the query is composed.
        var windowStartUtc = DateTime.UtcNow.AddDays(-LookbackDays);

        var accountsById = eligible.ToDictionary(static x => x.Account.Id);
        var folders = await _databaseService.Connection.Table<MailItemFolder>().ToListAsync().ConfigureAwait(false);
        var foldersById = folders.Where(x => accountsById.ContainsKey(x.MailAccountId)).ToDictionary(static x => x.Id);
        var mails = await _databaseService.Connection.Table<MailCopy>()
            .Where(x => x.CreationDate >= windowStartUtc)
            .ToListAsync().ConfigureAwait(false);

        var facts = new List<DailyBriefingFact>();
        var ignoredCount = 0;

        foreach (var accountGroup in mails
            .Where(x => foldersById.ContainsKey(x.FolderId))
            .GroupBy(x => foldersById[x.FolderId].MailAccountId))
        {
            var account = accountsById[accountGroup.Key].Account;
            if (!IntelligenceVisibilityPolicy.IsVisible(account.Preferences, IntelligenceFactKind.Briefing))
            {
                continue;
            }

            var distinct = accountGroup
                .Select(mail =>
                {
                    var folder = foldersById[mail.FolderId];
                    var remoteMessageId = RemoteMessageIdentity.TryCreate(
                        account.ProviderType,
                        mail.Id,
                        folder.RemoteFolderId,
                        mail.ImapUidValidity == 0 ? folder.UidValidity : mail.ImapUidValidity,
                        mail.ImapUid);
                    return (Mail: mail, RemoteMessageId: remoteMessageId);
                })
                .Where(static x => !string.IsNullOrWhiteSpace(x.RemoteMessageId))
                .GroupBy(static x => x.RemoteMessageId!, StringComparer.Ordinal)
                .Select(static x => x.First())
                .ToArray();

            if (distinct.Length == 0)
            {
                continue;
            }

            var remoteIds = distinct.Select(static x => x.RemoteMessageId!).ToArray();
            var jevArtifacts = await _store.GetClassificationArtifactsAsync(accountGroup.Key, remoteIds, cancellationToken).ConfigureAwait(false);
            var lunaArtifacts = await _store.GetSummaryArtifactsAsync(accountGroup.Key, remoteIds, cancellationToken).ConfigureAwait(false);
            var ignored = await _store.GetIgnoredAsync(accountGroup.Key, cancellationToken).ConfigureAwait(false);

            var enabledLabels = ResolveEnabledLabels(account);
            var indicatorState = new DailyBriefingIndicatorState(
                IntelligenceVisibilityPolicy.IsVisible(account.Preferences, IntelligenceFactKind.Priority),
                true,
                enabledLabels);

            foreach (var candidate in distinct)
            {
                var remoteMessageId = candidate.RemoteMessageId!;
                if (!jevArtifacts.TryGetValue(remoteMessageId, out var jev) || !jev.IncludeInBriefing)
                {
                    // Only messages Classification selected reach the briefing.
                    continue;
                }

                lunaArtifacts.TryGetValue(remoteMessageId, out var luna);

                // A card stays ignored only while the content it was ignored at is current.
                var isIgnored = ignored.TryGetValue(remoteMessageId, out var ignoredHash) &&
                    string.Equals(ignoredHash, jev.Key.ContentHash, StringComparison.OrdinalIgnoreCase);
                if (isIgnored)
                {
                    ignoredCount++;
                    if (!includeIgnored)
                    {
                        continue;
                    }
                }

                var mail = candidate.Mail;
                var receivedAt = new DateTimeOffset(DateTime.SpecifyKind(mail.CreationDate, DateTimeKind.Utc));
                var firstImported = await _store
                    .GetFirstImportedUtcAsync(accountGroup.Key, remoteMessageId, cancellationToken)
                    .ConfigureAwait(false) ?? jev.CompletedUtc;

                facts.Add(new DailyBriefingFact(
                    accountGroup.Key,
                    mail.UniqueId,
                    remoteMessageId,
                    jev.Key.ContentHash,
                    mail.Subject ?? string.Empty,
                    mail.FromName ?? string.Empty,
                    mail.FromAddress ?? string.Empty,
                    receivedAt,
                    jev.Labels,
                    jev.Priority,
                    jev.Action,
                    luna?.Headline ?? string.Empty,
                    luna?.Summary ?? string.Empty,
                    firstImported,
                    indicatorState,
                    isIgnored));
            }
        }

        var days = facts
            .GroupBy(fact => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(fact.ReceivedAt, timeZone).DateTime))
            .OrderByDescending(static group => group.Key)
            .Select(group => new DailyBriefingDay(
                group.Key,
                [.. group.OrderByDescending(static fact => fact.ReceivedAt)]))
            .ToArray();

        return new DailyBriefingFactsResult(days, facts.Count, ignoredCount);
    }

    private static IReadOnlySet<string> ResolveEnabledLabels(Core.Domain.Entities.Shared.MailAccount account)
    {
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in Enum.GetValues<Wino.Mail.AI.Abstractions.MailSmartLabel>())
        {
            if (IntelligenceVisibilityPolicy.IsVisible(account.Preferences, label))
            {
                enabled.Add(label.ToString().ToLowerInvariant());
            }
        }

        return enabled;
    }

    public Task IgnoreBriefingItemAsync(
        Guid localAccountId,
        string remoteMessageId,
        string contentHash,
        CancellationToken cancellationToken = default)
        => _store.SetIgnoredAsync(localAccountId, remoteMessageId, contentHash, true, cancellationToken);

    public Task UnignoreBriefingItemAsync(
        Guid localAccountId,
        string remoteMessageId,
        CancellationToken cancellationToken = default)
        => _store.SetIgnoredAsync(localAccountId, remoteMessageId, string.Empty, false, cancellationToken);

    public async Task<DailyBriefingUnseenState> GetUnseenStateAsync(CancellationToken cancellationToken = default)
    {
        var eligible = await GetEligibleAccountsAsync(cancellationToken).ConfigureAwait(false);
        DateTime? latestViewed = null;
        var hasUnseen = false;

        foreach (var entry in eligible)
        {
            var (_, lastViewedUtc) = await _store
                .GetBriefingViewStateAsync(entry.Account.Id, cancellationToken)
                .ConfigureAwait(false);
            if (lastViewedUtc is { } viewed && (latestViewed is null || viewed > latestViewed))
            {
                latestViewed = viewed;
            }

            var candidates = await _store.GetBriefingCandidatesAsync(entry.Account.Id, cancellationToken).ConfigureAwait(false);
            foreach (var candidate in candidates)
            {
                var firstImported = await _store
                    .GetFirstImportedUtcAsync(entry.Account.Id, candidate.Key.RemoteMessageId, cancellationToken)
                    .ConfigureAwait(false);

                // "New" is first-import time, not an artifact revision.
                if (firstImported is { } imported && (lastViewedUtc is null || imported > lastViewedUtc))
                {
                    hasUnseen = true;
                    break;
                }
            }

            if (hasUnseen)
            {
                break;
            }
        }

        return new DailyBriefingUnseenState(hasUnseen, latestViewed);
    }

    public async Task MarkOpenedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in await GetEligibleAccountsAsync(cancellationToken).ConfigureAwait(false))
        {
            await _store.MarkBriefingOpenedAsync(entry.Account.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task MarkViewedAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in await GetEligibleAccountsAsync(cancellationToken).ConfigureAwait(false))
        {
            await _store.MarkBriefingViewedAsync(entry.Account.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SaveAccessSnapshotAsync(LocalIntelligenceAccessSnapshot snapshot, CancellationToken cancellationToken = default)
        => await _store.SaveAccessAsync(
            snapshot.LocalAccountId,
            snapshot.WinoAccountId,
            snapshot.MailboxId ?? Guid.Empty,
            snapshot.HasAiPack,
            snapshot.HasIntelligenceConsent,
            cancellationToken).ConfigureAwait(false);

    public async Task<LocalIntelligenceAccessSnapshot?> GetAccessSnapshotAsync(Guid localAccountId, CancellationToken cancellationToken = default)
    {
        var access = await _store.GetAccessAsync(localAccountId, cancellationToken).ConfigureAwait(false);
        if (access is not { } value)
        {
            return null;
        }

        return new LocalIntelligenceAccessSnapshot(
            localAccountId,
            Guid.Empty,
            value.HasAiPack,
            value.HasConsent,
            value.MailboxId == Guid.Empty ? null : value.MailboxId,
            DateTimeOffset.UtcNow);
    }

    public Task InvalidateAccessSnapshotsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<bool> ShouldAutomaticallyProcessAsync(Guid localAccountId, CancellationToken cancellationToken = default)
    {
        var account = await _accountService.GetAccountAsync(localAccountId).ConfigureAwait(false);
        if (account is null ||
            !account.IsMailAccessGranted ||
            account.Preferences?.IsSemanticIndexingEnabled != true ||
            account.Preferences?.AutomaticallyIndexNewMessages != true)
        {
            return false;
        }

        var access = await _store.GetAccessAsync(localAccountId, cancellationToken).ConfigureAwait(false);
        return access is { HasAiPack: true, HasConsent: true } && access.Value.MailboxId != Guid.Empty;
    }

    public void Receive(IntelligenceMetadataChanged message) { }

    public void Receive(IntelligenceVisibilityChanged message) { }

    public void Dispose() => WeakReferenceMessenger.Default.UnregisterAll(this);
}
