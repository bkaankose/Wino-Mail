using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Tests.Helpers;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.Contracts.Intelligence;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class LocalIntelligenceServiceTests
{
    [Fact]
    public async Task GetBriefingFactsAsync_UsesBoundParametersForMailWindow()
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

        await database.Connection.InsertAsync(new WinoAccount
        {
            Id = winoAccountId,
            AccessToken = "local-test-token",
        });
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
        store.Setup(x => x.GetAccessSnapshotAsync(accountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalIntelligenceAccessSnapshot(
                accountId, winoAccountId, true, true, mailboxId, DateTimeOffset.UtcNow));
        var expectedRemoteMessageId = RemoteMessageIdentity.TryCreate(
            MailProviderType.Outlook, "message-1", null, 0, 0)!;
        var briefingId = Guid.NewGuid();
        var artifact = new IntelligenceArtifactDto
        {
            RemoteMessageId = expectedRemoteMessageId,
            ContentHash = "hash",
            Capability = IntelligenceCapability.BriefingFact,
            GenerationVersion = 1,
            PayloadSchemaVersion = 2,
            ArtifactRevision = 1,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            BriefingFact = new ConversationFactPayload
            {
                BriefingId = briefingId,
                OccurredAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
                Kind = MessageKind.Conversation,
                Status = BriefingStatus.Informational,
                Urgency = MailPriority.Normal,
                PrimaryAction = new NoActionPayload { Confidence = 1 },
                TemporalReferences =
                [
                    new DeadlineTemporalPayload
                    {
                        Due = new TemporalPointPayload(
                            DateOnly.FromDateTime(DateTime.UtcNow).AddDays(6),
                            null,
                            null,
                            string.Empty,
                            null,
                            TemporalPrecision.Date),
                        Confidence = 1,
                    },
                ],
                Confidence = 1,
            },
        };
        IReadOnlyCollection<string>? requestedMessageIds = null;
        store.Setup(x => x.GetCurrentArtifactsAsync(
                accountId, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, IReadOnlyCollection<string>, CancellationToken>((_, ids, _) => requestedMessageIds = ids)
            .ReturnsAsync(new Dictionary<string, IReadOnlyList<IntelligenceArtifactDto>>
            {
                [expectedRemoteMessageId] = [artifact],
            });
        store.Setup(x => x.GetBriefingHeadlinesAsync(
                accountId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, string> { [briefingId] = "Upcoming test item" });

        var accountService = new Mock<IAccountService>();
        accountService.Setup(x => x.GetAccountsAsync()).ReturnsAsync([account]);

        using var service = new LocalIntelligenceService(database, store.Object, accountService.Object);

        var facts = await service.GetBriefingFactsAsync(
            DateOnly.FromDateTime(DateTime.UtcNow), TimeZoneInfo.Utc);

        var fact = Assert.Single(facts);
        Assert.Equal(expectedRemoteMessageId, fact.RemoteMessageId);
        Assert.Equal("Upcoming test item", fact.Headline);
        Assert.Equal([expectedRemoteMessageId], requestedMessageIds);
    }
}
