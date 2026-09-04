using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Tests.Helpers;
using Wino.Mail.Contracts.Intelligence;
using Wino.Mail.AI.Abstractions;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class LocalIntelligenceServiceTests
{
    [Fact]
    public void MapAction_PreservesEveryV1ClientAction()
    {
        var expectedPayloads = new Dictionary<IntelligenceActionTypeV1, Type>
        {
            [IntelligenceActionTypeV1.Unknown] = typeof(NoActionPayload),
            [IntelligenceActionTypeV1.Reply] = typeof(ReplyActionPayload),
            [IntelligenceActionTypeV1.Pay] = typeof(PayActionPayload),
            [IntelligenceActionTypeV1.Review] = typeof(ReviewActionPayload),
            [IntelligenceActionTypeV1.FollowUp] = typeof(FollowUpActionPayload),
            [IntelligenceActionTypeV1.AddToCalendar] = typeof(AddToCalendarActionPayload),
            [IntelligenceActionTypeV1.ViewCalendarEvent] = typeof(ViewCalendarEventActionPayload),
            [IntelligenceActionTypeV1.AcceptInvitation] = typeof(AcceptInvitationActionPayload),
            [IntelligenceActionTypeV1.DeclineInvitation] = typeof(DeclineInvitationActionPayload),
            [IntelligenceActionTypeV1.RespondTentative] = typeof(RespondTentativeActionPayload),
            [IntelligenceActionTypeV1.Reschedule] = typeof(RescheduleActionPayload),
            [IntelligenceActionTypeV1.Confirm] = typeof(ConfirmActionPayload),
            [IntelligenceActionTypeV1.CompleteTask] = typeof(CompleteTaskActionPayload),
            [IntelligenceActionTypeV1.Approve] = typeof(ApproveActionPayload),
            [IntelligenceActionTypeV1.Reject] = typeof(RejectActionPayload),
            [IntelligenceActionTypeV1.Sign] = typeof(SignActionPayload),
            [IntelligenceActionTypeV1.Submit] = typeof(SubmitActionPayload),
            [IntelligenceActionTypeV1.ViewDocument] = typeof(ViewDocumentActionPayload),
            [IntelligenceActionTypeV1.DownloadAttachment] = typeof(DownloadAttachmentActionPayload),
            [IntelligenceActionTypeV1.ReviewInvoice] = typeof(ReviewInvoiceActionPayload),
            [IntelligenceActionTypeV1.Verify] = typeof(VerifyAccountActionPayload),
            [IntelligenceActionTypeV1.Attend] = typeof(ViewCalendarEventActionPayload),
            [IntelligenceActionTypeV1.ViewOrder] = typeof(ViewOrderActionPayload),
            [IntelligenceActionTypeV1.TrackShipment] = typeof(TrackShipmentActionPayload),
            [IntelligenceActionTypeV1.ViewItinerary] = typeof(ViewItineraryActionPayload),
            [IntelligenceActionTypeV1.CheckIn] = typeof(CheckInActionPayload),
            [IntelligenceActionTypeV1.ViewReservation] = typeof(ViewReservationActionPayload),
            [IntelligenceActionTypeV1.CancelReservation] = typeof(CancelReservationActionPayload),
            [IntelligenceActionTypeV1.Renew] = typeof(RenewActionPayload),
            [IntelligenceActionTypeV1.Cancel] = typeof(CancelSubscriptionActionPayload),
            [IntelligenceActionTypeV1.CancelSubscription] = typeof(CancelSubscriptionActionPayload),
            [IntelligenceActionTypeV1.Download] = typeof(DownloadAttachmentActionPayload),
            [IntelligenceActionTypeV1.Contact] = typeof(OpenRelevantLinkActionPayload),
            [IntelligenceActionTypeV1.VerifyAccount] = typeof(VerifyAccountActionPayload),
            [IntelligenceActionTypeV1.CopyVerificationCode] = typeof(CopyVerificationCodeActionPayload),
            [IntelligenceActionTypeV1.OpenMagicSignInLink] = typeof(OpenMagicSignInLinkActionPayload),
            [IntelligenceActionTypeV1.ChangePassword] = typeof(ChangePasswordActionPayload),
            [IntelligenceActionTypeV1.ReviewAccountActivity] = typeof(ReviewAccountActivityActionPayload),
            [IntelligenceActionTypeV1.ReportPhishing] = typeof(ReportPhishingActionPayload),
            [IntelligenceActionTypeV1.OpenRelevantLink] = typeof(OpenRelevantLinkActionPayload),
            [IntelligenceActionTypeV1.Unsubscribe] = typeof(UnsubscribeActionPayload),
        };
        var temporals = new[]
        {
            new TemporalReferenceV1("time", TemporalReferenceTypeV1.Meeting, "tomorrow", DateTimeOffset.UtcNow,
                null, TemporalPrecisionV1.Minute, "UTC", 0.9),
        };
        var documents = new[]
        {
            new IntelligenceDocumentV1("document", IntelligenceDocumentTypeV1.VerificationCode,
                IntelligenceStatusV1.Active, "Verification code", "123456", string.Empty, null, string.Empty,
                ["time"], 0.9),
        };

        Assert.Equal(Enum.GetValues<IntelligenceActionTypeV1>().Length, expectedPayloads.Count);

        foreach (var (actionType, expectedPayload) in expectedPayloads)
        {
            var action = new IntelligenceActionV1(actionType, IntelligenceStatusV1.Pending, string.Empty,
                string.Empty, "document", "time", 0.9);
            var payload = LocalIntelligenceService.MapAction(action, temporals, documents);

            Assert.IsType(expectedPayload, payload);
        }

        var calendar = Assert.IsType<AddToCalendarActionPayload>(LocalIntelligenceService.MapAction(
            new IntelligenceActionV1(IntelligenceActionTypeV1.AddToCalendar, IntelligenceStatusV1.Confirmed,
                string.Empty, string.Empty, string.Empty, "time", 0.9), temporals, documents));
        Assert.Equal(0, calendar.TemporalReferenceIndex);

        var code = Assert.IsType<CopyVerificationCodeActionPayload>(LocalIntelligenceService.MapAction(
            new IntelligenceActionV1(IntelligenceActionTypeV1.CopyVerificationCode, IntelligenceStatusV1.Active,
                string.Empty, string.Empty, "document", "time", 0.9), temporals, documents));
        Assert.Equal("123456", code.Code);
    }

    [Fact]
    public async Task GetBriefingFactsAsync_UsesV1DocumentsAndBoundMailWindow()
    {
        await using var database = new InMemoryDatabaseService();
        await database.InitializeAsync();

        var accountId = Guid.NewGuid();
        var winoAccountId = Guid.NewGuid();
        var mailboxId = Guid.NewGuid();
        var folderId = Guid.NewGuid();
        var account = new MailAccount
        {
            Id = accountId,
            Name = "Test account",
            Address = "test@example.com",
            ProviderType = MailProviderType.Outlook,
            IsMailAccessGranted = true,
        };

        await database.Connection.InsertAsync(new WinoAccount { Id = winoAccountId, AccessToken = "local-test-token" });
        await database.Connection.InsertAsync(new MailItemFolder
        {
            Id = folderId,
            MailAccountId = accountId,
            FolderName = "Inbox",
        });
        await database.Connection.InsertAsync(new MailCopy
        {
            UniqueId = Guid.NewGuid(),
            Id = "message-1",
            FolderId = folderId,
            Subject = "Test",
            FromName = "Sender",
            CreationDate = DateTime.UtcNow.AddDays(-1),
        });

        var store = new Mock<ILocalIntelligenceStore>();
        store.Setup(x => x.GetDailyBriefingIgnoreRevisionsAsync(accountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, long>());
        store.Setup(x => x.GetAccessSnapshotAsync(accountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalIntelligenceAccessSnapshot(
                accountId, winoAccountId, true, true, mailboxId, DateTimeOffset.UtcNow));
        var expectedRemoteMessageId = RemoteMessageIdentity.TryCreate(
            MailProviderType.Outlook, "message-1", null, 0, 0)!;
        var currentDocument = CreateDocument(expectedRemoteMessageId, 1);
        IReadOnlyCollection<string>? requestedMessageIds = null;
        store.Setup(x => x.GetCurrentDocumentsAsync(
                accountId, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, IReadOnlyCollection<string>, CancellationToken>((_, ids, _) => requestedMessageIds = ids)
            .ReturnsAsync(() => new Dictionary<string, MessageIntelligenceDownloadDto>
            {
                [expectedRemoteMessageId] = currentDocument,
            });

        var accountService = new Mock<IAccountService>();
        accountService.Setup(x => x.GetAccountsAsync()).ReturnsAsync([account]);

        using var service = new LocalIntelligenceService(database, store.Object, accountService.Object);
        var facts = await service.GetBriefingFactsAsync(
            DateOnly.FromDateTime(DateTime.UtcNow), TimeZoneInfo.Utc);

        var fact = Assert.Single(facts.Facts);
        Assert.Equal(expectedRemoteMessageId, fact.RemoteMessageId);
        Assert.Equal("Upcoming test item", fact.Headline);
        Assert.Equal([expectedRemoteMessageId], requestedMessageIds);
        var briefingId = fact.Fact.BriefingId;

        var ignoredRevisions = new Dictionary<Guid, long> { [briefingId] = 1 };
        store.Setup(x => x.GetDailyBriefingIgnoreRevisionsAsync(accountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ignoredRevisions);

        var hidden = await service.GetBriefingFactsAsync(
            DateOnly.FromDateTime(DateTime.UtcNow), TimeZoneInfo.Utc);
        Assert.Empty(hidden.Facts);
        Assert.True(hidden.HasIgnoredFacts);

        var included = await service.GetBriefingFactsAsync(
            DateOnly.FromDateTime(DateTime.UtcNow), TimeZoneInfo.Utc, includeIgnored: true);
        Assert.True(Assert.Single(included.Facts).IsIgnored);

        currentDocument = CreateDocument(expectedRemoteMessageId, 2);
        var revised = await service.GetBriefingFactsAsync(
            DateOnly.FromDateTime(DateTime.UtcNow), TimeZoneInfo.Utc);
        Assert.False(Assert.Single(revised.Facts).IsIgnored);
    }

    private static MessageIntelligenceDownloadDto CreateDocument(string serverMessageKey, long revision)
        => new()
        {
            ServerMessageKey = serverMessageKey,
            ContentHash = "hash",
            Subject = "Test",
            Sender = "sender@example.com",
            ReceivedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
            IsOutgoing = false,
            IsRead = false,
            IsFlagged = false,
            HasAttachments = false,
            FolderIds = ["inbox"],
            SenderAddresses = ["sender@example.com"],
            RecipientAddresses = ["test@example.com"],
            Analysis = new MessageIntelligenceDocumentV1
            {
                SourceLanguage = "en",
                Headline = "Upcoming test item",
                Summary = "Summary",
                Category = MessageCategoryV1.Conversation,
                Intent = MessageIntentV1.Inform,
                Urgency = MessageUrgencyV1.Normal,
                Confidence = 1,
                TemporalReferences =
                [
                    new TemporalReferenceV1(
                        "due",
                        TemporalReferenceTypeV1.Due,
                        "in six days",
                        DateTimeOffset.UtcNow.Date.AddDays(6),
                        null,
                        TemporalPrecisionV1.Day,
                        "UTC",
                        1),
                ],
            },
            Embedding = Convert.ToBase64String(new byte[3_072]),
            EmbeddingDimensions = 768,
            EmbeddingEncoding = "float32-le",
            ArtifactRevision = revision,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
        };
}
