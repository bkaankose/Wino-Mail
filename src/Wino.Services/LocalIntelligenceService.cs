#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.Contracts.Intelligence;
using Wino.Messaging.UI;

namespace Wino.Services;

public sealed class LocalIntelligenceService : ILocalIntelligenceService,
    IRecipient<IntelligenceMetadataChanged>,
    IRecipient<IntelligenceVisibilityChanged>,
    IDisposable
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
        DateOnly localDate,
        TimeZoneInfo timeZone,
        bool includeIgnored = false,
        CancellationToken cancellationToken = default)
    {
        var eligible = await GetEligibleAccountsAsync(cancellationToken).ConfigureAwait(false);
        if (eligible.Count == 0) return new([], false);
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
        var hasIgnoredFacts = false;
        foreach (var accountGroup in mails.Where(x => foldersById.ContainsKey(x.FolderId))
            .GroupBy(x => foldersById[x.FolderId].MailAccountId))
        {
            var account = accountsById[accountGroup.Key].Account;
            if (!IntelligenceVisibilityPolicy.IsVisible(account.Preferences, IntelligenceFactKind.Briefing))
                continue;

            var ignoredRevisions = await _store.GetDailyBriefingIgnoreRevisionsAsync(
                accountGroup.Key, cancellationToken).ConfigureAwait(false);

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
            var documents = await _store.GetCurrentDocumentsAsync(accountGroup.Key,
                distinct.Select(static x => x.RemoteMessageId!).ToArray(), cancellationToken).ConfigureAwait(false);
            var included = new List<(MailCopy Mail, string RemoteMessageId, DateTimeOffset OccurredAt,
                BriefingFactCapabilityPayload Fact, long ArtifactRevision,
                IReadOnlyList<SmartLabelScore> SourceSmartLabels,
                DailyBriefingIndicatorState IndicatorState)>();
            foreach (var candidate in distinct)
            {
                var mail = candidate.Mail;
                var remoteMessageId = candidate.RemoteMessageId!;
                if (!documents.TryGetValue(remoteMessageId, out var document)) continue;
                var fact = CreateBriefingFact(accountGroup.Key, document);
                var sourceSmartLabels = document.Analysis.SmartLabels
                    .Where(static label => label.Label != SmartLabelV1.Unknown)
                    .Select(static label => Enum.TryParse<MailSmartLabel>(label.Label.ToString(), out var mapped)
                        ? new SmartLabelScore(mapped, label.Confidence)
                        : null)
                    .OfType<SmartLabelScore>()
                    .DistinctBy(static label => label.Label)
                    .ToArray();
                var includedSmartLabels = sourceSmartLabels
                    .Where(label => IntelligenceVisibilityPolicy.IsVisible(account.Preferences, label.Label))
                    .ToArray();
                var indicatorState = new DailyBriefingIndicatorState(
                    IntelligenceVisibilityPolicy.IsVisible(account.Preferences, IntelligenceFactKind.Deadline),
                    IntelligenceVisibilityPolicy.IsVisible(account.Preferences, IntelligenceFactKind.NeedsReply),
                    IntelligenceVisibilityPolicy.IsVisible(account.Preferences, IntelligenceFactKind.Priority),
                    true,
                    includedSmartLabels);

                var occurredAt = new DateTimeOffset(DateTime.SpecifyKind(mail.CreationDate, DateTimeKind.Utc));
                var occurredDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(occurredAt, timeZone).DateTime);
                var temporalDates = EnumerateTemporalPoints(fact).Select(point => ResolveLocalDate(point, timeZone)).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
                var inSelectedDay = occurredDate == localDate || temporalDates.Contains(localDate);
                var inUpcomingWindow = localDate == today && temporalDates.Any(date => date >= today && date <= today.AddDays(7));
                if (!inSelectedDay && !inUpcomingWindow) continue;

                var isIgnored = fact.BriefingId != Guid.Empty &&
                    ignoredRevisions.TryGetValue(fact.BriefingId, out var ignoredRevision) &&
                    document.ArtifactRevision <= ignoredRevision;
                if (isIgnored)
                {
                    hasIgnoredFacts = true;
                    if (!includeIgnored) continue;
                }

                included.Add((mail, remoteMessageId, occurredAt, fact, document.ArtifactRevision,
                    sourceSmartLabels, indicatorState));
            }

            foreach (var item in included)
            {
                var mail = item.Mail;
                var fact = item.Fact;
                var isIgnored = ignoredRevisions.TryGetValue(fact.BriefingId, out var ignoredRevision) &&
                    item.ArtifactRevision <= ignoredRevision;
                result.Add(new(accountGroup.Key, mail.UniqueId, item.RemoteMessageId, mail.Subject, mail.FromName,
                    item.OccurredAt, documents[item.RemoteMessageId].Analysis.Headline, item.ArtifactRevision, fact,
                    item.SourceSmartLabels, item.IndicatorState, isIgnored));
            }
        }
        return new(result.OrderByDescending(static x => x.OccurredAt).ToArray(), hasIgnoredFacts);
    }

    public Task IgnoreBriefingItemAsync(
        Guid localAccountId,
        Guid briefingId,
        long artifactRevision,
        CancellationToken cancellationToken = default)
        => _store.SaveDailyBriefingIgnoreAsync(localAccountId, briefingId, artifactRevision,
            DateTimeOffset.UtcNow, cancellationToken);

    public Task UnignoreBriefingItemAsync(
        Guid localAccountId,
        Guid briefingId,
        CancellationToken cancellationToken = default)
        => _store.DeleteDailyBriefingIgnoreAsync(localAccountId, briefingId, cancellationToken);

    public Task DeleteBriefingItemAsync(
        Guid localAccountId,
        string remoteMessageId,
        CancellationToken cancellationToken = default)
        => _store.DeleteDailyBriefingItemAsync(localAccountId, remoteMessageId, cancellationToken);

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

    private static BriefingFactCapabilityPayload CreateBriefingFact(
        Guid localAccountId,
        MessageIntelligenceDownloadDto document)
    {
        var analysis = document.Analysis;
        BriefingFactCapabilityPayload fact = analysis.Category switch
        {
            MessageCategoryV1.Finance => new FinanceFactPayload(),
            MessageCategoryV1.Document => new DocumentFactPayload(),
            MessageCategoryV1.Purchase => new PurchaseFactPayload(),
            MessageCategoryV1.Travel => new TravelFactPayload(),
            MessageCategoryV1.Subscription => new SubscriptionFactPayload(),
            MessageCategoryV1.Security => new SecurityFactPayload(),
            MessageCategoryV1.Meeting => new MeetingFactPayload(),
            MessageCategoryV1.Task => new TaskFactPayload(),
            MessageCategoryV1.Newsletter => new NewsletterFactPayload(),
            MessageCategoryV1.Promotion => new PromotionFactPayload(),
            MessageCategoryV1.Social => new SocialFactPayload(),
            MessageCategoryV1.SystemNotification => new SystemNotificationFactPayload(),
            MessageCategoryV1.Support => new SupportFactPayload(),
            MessageCategoryV1.Conversation => new ConversationFactPayload(),
            _ => new GeneralFactPayload(),
        };

        fact.BriefingId = CreateBriefingId(localAccountId, document.ServerMessageKey);
        fact.OccurredAtUtc = document.ReceivedAtUtc;
        fact.Kind = MapMessageKind(analysis.Category, analysis.Intent);
        fact.Status = MapStatus(document.IsOutgoing, analysis);
        fact.Urgency = analysis.Urgency switch
        {
            MessageUrgencyV1.Critical => MailPriority.Urgent,
            MessageUrgencyV1.High => MailPriority.High,
            MessageUrgencyV1.Low => MailPriority.Low,
            _ => MailPriority.Normal,
        };
        fact.PrimaryAction = MapAction(analysis.Actions.FirstOrDefault(), analysis.TemporalReferences, analysis.Documents);
        fact.TemporalReferences = analysis.TemporalReferences.Select(MapTemporal).ToArray();
        fact.Confidence = analysis.Confidence;
        return fact;
    }

    private static Guid CreateBriefingId(Guid localAccountId, string serverMessageKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{localAccountId:D}\n{serverMessageKey}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static MessageKind MapMessageKind(MessageCategoryV1 category, MessageIntentV1 intent)
        => category switch
        {
            MessageCategoryV1.Finance => MessageKind.Invoice,
            MessageCategoryV1.Document => MessageKind.SharedDocument,
            MessageCategoryV1.Purchase => MessageKind.OrderUpdate,
            MessageCategoryV1.Travel => MessageKind.Itinerary,
            MessageCategoryV1.Subscription => MessageKind.SubscriptionRenewal,
            MessageCategoryV1.Security => MessageKind.SignInAlert,
            MessageCategoryV1.Meeting => MessageKind.MeetingInvitation,
            MessageCategoryV1.Task => MessageKind.TaskAssignment,
            MessageCategoryV1.Newsletter => MessageKind.Publication,
            MessageCategoryV1.Promotion => MessageKind.Offer,
            MessageCategoryV1.Social => MessageKind.PersonalUpdate,
            MessageCategoryV1.SystemNotification => MessageKind.OperationalNotice,
            MessageCategoryV1.Support => MessageKind.SupportStatusUpdate,
            _ when intent == MessageIntentV1.ReplyRequested => MessageKind.ReplyRequest,
            _ => MessageKind.Information,
        };

    private static BriefingStatus MapStatus(bool isOutgoing, MessageIntelligenceDocumentV1 analysis)
    {
        var status = analysis.Actions.Select(static action => action.Status)
            .Concat(analysis.Documents.Select(static document => document.Status))
            .FirstOrDefault(static status => status != IntelligenceStatusV1.Unknown);
        return status switch
        {
            IntelligenceStatusV1.Completed or IntelligenceStatusV1.Paid or IntelligenceStatusV1.Delivered => BriefingStatus.Completed,
            IntelligenceStatusV1.Cancelled => BriefingStatus.Cancelled,
            IntelligenceStatusV1.Expired => BriefingStatus.Expired,
            IntelligenceStatusV1.Active or IntelligenceStatusV1.Shipped => BriefingStatus.InProgress,
            IntelligenceStatusV1.Confirmed or IntelligenceStatusV1.Reserved => BriefingStatus.Scheduled,
            IntelligenceStatusV1.RequiresAction or IntelligenceStatusV1.Unpaid or IntelligenceStatusV1.Overdue or
                IntelligenceStatusV1.AwaitingApproval or IntelligenceStatusV1.AwaitingSignature => BriefingStatus.ActionRequired,
            _ when analysis.Intent == MessageIntentV1.ReplyRequested => isOutgoing
                ? BriefingStatus.AwaitingOthers
                : BriefingStatus.AwaitingMyReply,
            _ when analysis.Actions.Count > 0 => BriefingStatus.ActionRequired,
            _ => BriefingStatus.Informational,
        };
    }

    internal static BriefingActionPayload MapAction(
        IntelligenceActionV1? action,
        IReadOnlyList<TemporalReferenceV1> temporalReferences,
        IReadOnlyList<IntelligenceDocumentV1> documents)
        => action?.Type switch
        {
            IntelligenceActionTypeV1.Reply => new ReplyActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.Pay => new PayActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.Review => new ReviewActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.FollowUp => new FollowUpActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.AddToCalendar => new AddToCalendarActionPayload
            {
                Confidence = action.Confidence,
                TemporalReferenceIndex = FindTemporalReferenceIndex(action.TemporalReferenceId, temporalReferences),
            },
            IntelligenceActionTypeV1.ViewCalendarEvent => new ViewCalendarEventActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.AcceptInvitation => new AcceptInvitationActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.DeclineInvitation => new DeclineInvitationActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.RespondTentative => new RespondTentativeActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.Reschedule => new RescheduleActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.Confirm => new ConfirmActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.CompleteTask => new CompleteTaskActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.Approve => new ApproveActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.Reject => new RejectActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.Sign => new SignActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.Submit => new SubmitActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.ViewDocument => new ViewDocumentActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.DownloadAttachment or IntelligenceActionTypeV1.Download =>
                new DownloadAttachmentActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.ReviewInvoice => new ReviewInvoiceActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.Verify or IntelligenceActionTypeV1.VerifyAccount =>
                new VerifyAccountActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.Attend => new ViewCalendarEventActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.ViewOrder => new ViewOrderActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.TrackShipment => new TrackShipmentActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.ViewItinerary => new ViewItineraryActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.CheckIn => new CheckInActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.ViewReservation => new ViewReservationActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.CancelReservation => new CancelReservationActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.Renew => new RenewActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.Cancel or IntelligenceActionTypeV1.CancelSubscription =>
                new CancelSubscriptionActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.Contact => new OpenRelevantLinkActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.CopyVerificationCode => MapVerificationCode(action, temporalReferences, documents),
            IntelligenceActionTypeV1.OpenMagicSignInLink => new OpenMagicSignInLinkActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.ChangePassword => new ChangePasswordActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.ReviewAccountActivity => new ReviewAccountActivityActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.ReportPhishing => new ReportPhishingActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.OpenRelevantLink => new OpenRelevantLinkActionPayload { Confidence = action.Confidence },
            IntelligenceActionTypeV1.Unsubscribe => new UnsubscribeActionPayload { Confidence = action.Confidence },
            _ => new NoActionPayload(),
        };

    private static CopyVerificationCodeActionPayload MapVerificationCode(
        IntelligenceActionV1 action,
        IReadOnlyList<TemporalReferenceV1> temporalReferences,
        IReadOnlyList<IntelligenceDocumentV1> documents)
    {
        var document = documents.FirstOrDefault(document => document.Id == action.DocumentId);

        return new CopyVerificationCodeActionPayload
        {
            Code = document?.Reference ?? string.Empty,
            Confidence = action.Confidence,
            ExpirationTemporalReferenceIndex = FindTemporalReferenceIndex(action.TemporalReferenceId, temporalReferences),
        };
    }

    private static int FindTemporalReferenceIndex(
        string temporalReferenceId,
        IReadOnlyList<TemporalReferenceV1> temporalReferences)
    {
        for (var index = 0; index < temporalReferences.Count; index++)
        {
            if (temporalReferences[index].Id == temporalReferenceId)
            {
                return index;
            }
        }

        return -1;
    }

    private static TemporalPayload MapTemporal(TemporalReferenceV1 temporal)
    {
        var start = ToTemporalPoint(temporal.Start, temporal);
        var end = ToTemporalPoint(temporal.End, temporal);
        return temporal.Type switch
        {
            TemporalReferenceTypeV1.Due or TemporalReferenceTypeV1.Deadline =>
                new DeadlineTemporalPayload { Due = start, Confidence = temporal.Confidence },
            TemporalReferenceTypeV1.Meeting =>
                new EventTemporalPayload { Start = start, End = temporal.End is null ? null : end, Confidence = temporal.Confidence },
            TemporalReferenceTypeV1.Renewal =>
                new RenewalTemporalPayload { RenewsAt = start, Confidence = temporal.Confidence },
            TemporalReferenceTypeV1.Expiration =>
                new ExpirationTemporalPayload { ExpiresAt = start, Confidence = temporal.Confidence },
            _ => new ExpectedTemporalPayload { ExpectedAt = start, Confidence = temporal.Confidence },
        };
    }

    private static TemporalPointPayload ToTemporalPoint(DateTimeOffset? value, TemporalReferenceV1 temporal)
        => new(
            value is { } instant ? DateOnly.FromDateTime(instant.Date) : null,
            value is { } time ? TimeOnly.FromDateTime(time.DateTime) : null,
            value,
            temporal.TimeZoneId,
            value is { } offset ? (int)offset.Offset.TotalMinutes : null,
            temporal.Precision switch
            {
                TemporalPrecisionV1.Minute => TemporalPrecision.ExactDateTime,
                TemporalPrecisionV1.Day => TemporalPrecision.Date,
                TemporalPrecisionV1.Month => TemporalPrecision.Month,
                _ => TemporalPrecision.Unknown,
            });

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
        return account.Preferences.AutomaticallyIndexNewMessages;
    }

    public void Receive(IntelligenceMetadataChanged message) => WeakReferenceMessenger.Default.Send(new DailyBriefingStateChanged());

    public void Receive(IntelligenceVisibilityChanged message) => WeakReferenceMessenger.Default.Send(new DailyBriefingStateChanged());
    public void Dispose() => WeakReferenceMessenger.Default.UnregisterAll(this);
}
