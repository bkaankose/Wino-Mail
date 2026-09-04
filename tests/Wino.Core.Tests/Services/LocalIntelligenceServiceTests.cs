using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Tests.Helpers;
using Wino.Mail.Contracts.Intelligence;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class LocalIntelligenceServiceTests
{
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
