#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.AI.Abstractions;
using Wino.Messaging.UI;

namespace Wino.Services;

public sealed class LocalIntelligenceService : ILocalIntelligenceService,
    IRecipient<IntelligenceMetadataChanged>, IDisposable
{
    private readonly IDatabaseService _databaseService;
    private readonly ILocalIntelligenceStore _store;
    private readonly IAccountService _accountService;

    public LocalIntelligenceService(IDatabaseService databaseService, ILocalIntelligenceStore store,
        IAccountService accountService)
    {
        _databaseService = databaseService;
        _store = store;
        _accountService = accountService;
        WeakReferenceMessenger.Default.Register(this);
    }

    public async Task<IReadOnlyList<DailyBriefingAccount>> GetEligibleAccountsAsync(CancellationToken cancellationToken = default)
    {
        await _databaseService.InitializeAsync().ConfigureAwait(false);
        var profile = await _databaseService.Connection.Table<WinoAccount>().FirstOrDefaultAsync().ConfigureAwait(false);
        if (profile is null || (string.IsNullOrWhiteSpace(profile.AccessToken) && string.IsNullOrWhiteSpace(profile.RefreshToken)))
            return [];
        var accounts = await _accountService.GetAccountsAsync().ConfigureAwait(false);
        var result = new List<DailyBriefingAccount>();
        foreach (var account in accounts.Where(static x => x.IsMailAccessGranted).OrderBy(static x => x.Order))
        {
            var access = await _store.GetAccessSnapshotAsync(account.Id, cancellationToken).ConfigureAwait(false);
            if (access is { IsEligible: true } && access.WinoAccountId == profile.Id)
                result.Add(new(account, access.MailboxId!.Value));
        }
        return result;
    }

    public async Task<IReadOnlyList<DailyBriefingFact>> GetBriefingFactsAsync(DateOnly localDate, TimeZoneInfo timeZone, CancellationToken cancellationToken = default)
    {
        var eligible = await GetEligibleAccountsAsync(cancellationToken).ConfigureAwait(false);
        if (eligible.Count == 0) return [];
        await _databaseService.InitializeAsync().ConfigureAwait(false);
        var startLocal = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var endLocal = localDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, timeZone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(endLocal, timeZone);
        // sqlite-net translates method calls inside the expression tree into SQL functions.
        // SQLite has no AddDays function, so calculate the query bounds before composing it.
        var mailWindowStartUtc = startUtc.AddDays(-90);
        var mailWindowEndUtc = endUtc.AddDays(8);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone).DateTime);
        var accountsById = eligible.ToDictionary(static x => x.Account.Id);
        var folders = await _databaseService.Connection.Table<MailItemFolder>().ToListAsync().ConfigureAwait(false);
        var foldersById = folders.Where(x => accountsById.ContainsKey(x.MailAccountId)).ToDictionary(static x => x.Id);
        var mails = await _databaseService.Connection.Table<MailCopy>()
            .Where(x => x.CreationDate >= mailWindowStartUtc && x.CreationDate < mailWindowEndUtc)
            .ToListAsync().ConfigureAwait(false);
        var result = new List<DailyBriefingFact>();
        foreach (var accountGroup in mails.Where(x => foldersById.ContainsKey(x.FolderId))
            .GroupBy(x => foldersById[x.FolderId].MailAccountId))
        {
            var account = accountsById[accountGroup.Key].Account;
            var distinct = accountGroup.Select(mail =>
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
            var artifacts = await _store.GetCurrentArtifactsAsync(accountGroup.Key,
                distinct.Select(static x => x.RemoteMessageId!).ToArray(), cancellationToken).ConfigureAwait(false);
            var included = new List<(MailCopy Mail, string RemoteMessageId, DateTimeOffset OccurredAt,
                BriefingFactCapabilityPayload Fact, long ArtifactRevision)>();
            foreach (var candidate in distinct)
            {
                var mail = candidate.Mail;
                var remoteMessageId = candidate.RemoteMessageId!;
                if (!artifacts.TryGetValue(remoteMessageId, out var values)) continue;
                var live = values.Where(static x => !x.IsDeleted).ToArray();
                var factArtifact = live.Where(static x => x.Capability == IntelligenceCapability.BriefingFact)
                    .MaxBy(static x => x.ArtifactRevision);
                if (factArtifact?.BriefingFact is not { } fact) continue;

                var occurredAt = new DateTimeOffset(DateTime.SpecifyKind(mail.CreationDate, DateTimeKind.Utc));
                var occurredDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(occurredAt, timeZone).DateTime);
                var temporalDates = EnumerateTemporalPoints(fact).Select(point => ResolveLocalDate(point, timeZone)).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
                var inSelectedDay = occurredDate == localDate || temporalDates.Contains(localDate);
                var inUpcomingWindow = localDate == today && temporalDates.Any(date => date >= today && date <= today.AddDays(7));
                if (!inSelectedDay && !inUpcomingWindow) continue;

                included.Add((mail, remoteMessageId, occurredAt, fact, factArtifact.ArtifactRevision));
            }

            var headlines = await _store.GetBriefingHeadlinesAsync(accountGroup.Key,
                included.Select(static x => x.Fact.BriefingId).ToArray(), cancellationToken).ConfigureAwait(false);
            foreach (var item in included)
            {
                var mail = item.Mail;
                var fact = item.Fact;
                headlines.TryGetValue(fact.BriefingId, out var headline);
                result.Add(new(accountGroup.Key, mail.UniqueId, item.RemoteMessageId, mail.Subject, mail.FromName,
                    item.OccurredAt, headline ?? string.Empty, item.ArtifactRevision, fact));
            }
        }
        return result.OrderByDescending(static x => x.OccurredAt).ToArray();
    }

    private static IEnumerable<TemporalPointPayload> EnumerateTemporalPoints(BriefingFactCapabilityPayload fact)
        => fact.TemporalReferences.SelectMany(static temporal => temporal switch
        {
            DeadlineTemporalPayload x => new[] { x.Due },
            EventTemporalPayload x => x.End is null ? new[] { x.Start } : new[] { x.Start, x.End },
            DateRangeTemporalPayload x => new[] { x.Start, x.End },
            AvailabilityWindowTemporalPayload x => new[] { x.Opens, x.Closes },
            CoveragePeriodTemporalPayload x => new[] { x.Start, x.End },
            ExpectedTemporalPayload x => new[] { x.ExpectedAt },
            ExpirationTemporalPayload x => new[] { x.ExpiresAt },
            RenewalTemporalPayload x => new[] { x.RenewsAt },
            TravelTemporalPayload x => new[] { x.Departure, x.Arrival },
            _ => Array.Empty<TemporalPointPayload>(),
        });

    private static DateOnly? ResolveLocalDate(TemporalPointPayload point, TimeZoneInfo fallbackZone)
        => point.LocalDate ?? (point.InstantUtc is { } instant
            ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, fallbackZone).DateTime)
            : null);

    public Task<long> GetLatestBriefingFactRevisionAsync(Guid localAccountId, CancellationToken cancellationToken = default)
        => _store.GetLatestBriefingFactRevisionAsync(localAccountId, cancellationToken);

    public Task SaveAccessSnapshotAsync(LocalIntelligenceAccessSnapshot snapshot, CancellationToken cancellationToken = default) => _store.SaveAccessSnapshotAsync(snapshot, cancellationToken);
    public Task<LocalIntelligenceAccessSnapshot?> GetAccessSnapshotAsync(Guid localAccountId, CancellationToken cancellationToken = default) => _store.GetAccessSnapshotAsync(localAccountId, cancellationToken);
    public Task InvalidateAccessSnapshotsAsync(CancellationToken cancellationToken = default) => _store.DeleteAccessSnapshotsAsync(cancellationToken);

    public async Task<DailyBriefingUnseenState> GetUnseenStateAsync(CancellationToken cancellationToken = default)
    {
        var ids = (await GetEligibleAccountsAsync(cancellationToken).ConfigureAwait(false)).Select(static x => x.Account.Id).ToArray();
        return await _store.GetDailyBriefingUnseenStateAsync(ids, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkOpenedAsync(CancellationToken cancellationToken = default)
    {
        var ids = (await GetEligibleAccountsAsync(cancellationToken).ConfigureAwait(false)).Select(static x => x.Account.Id).ToArray();
        await _store.MarkDailyBriefingOpenedAsync(ids, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkViewedAsync(CancellationToken cancellationToken = default)
    {
        var ids = (await GetEligibleAccountsAsync(cancellationToken).ConfigureAwait(false)).Select(static x => x.Account.Id).ToArray();
        await _store.MarkDailyBriefingViewedAsync(ids, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        WeakReferenceMessenger.Default.Send(new DailyBriefingStateChanged());
    }

    public async Task<bool> ShouldAutomaticallyProcessAsync(Guid localAccountId, CancellationToken cancellationToken = default)
    {
        var account = await _accountService.GetAccountAsync(localAccountId).ConfigureAwait(false);
        if (account is null || !account.IsMailAccessGranted || !account.Preferences.IsSemanticIndexingEnabled)
            return false;
        var access = await _store.GetAccessSnapshotAsync(localAccountId, cancellationToken).ConfigureAwait(false);
        if (access is not { IsEligible: true }) return false;
        var intent = (await _store.GetJobIntentsAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(x => x.LocalAccountId == localAccountId);
        return intent?.AutomaticallyIndexNewMessages == true;
    }

    public void Receive(IntelligenceMetadataChanged message) => WeakReferenceMessenger.Default.Send(new DailyBriefingStateChanged());
    public void Dispose() => WeakReferenceMessenger.Default.UnregisterAll(this);
}
