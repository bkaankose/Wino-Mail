using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using SQLite;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Contracts.Intelligence;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public sealed class LocalIntelligenceStoreV1Tests
{
    [Fact]
    public async Task ChangePages_PersistAcrossRestartAndApplyTombstonesAtomically()
    {
        var directory = CreateTemporaryDirectory();
        var accountId = Guid.NewGuid();
        var mailboxId = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        try
        {
            await using (var store = CreateStore(directory))
            {
                await store.InitializeAsync();
                await store.AlignMailboxHeadAsync(accountId, Head(mailboxId, epoch, 2));
                await store.ApplyChangesAsync(accountId, mailboxId, Page(epoch, 2, 1,
                    new IntelligenceDocumentChangeDto(1, "m1", false, Document("m1", 1), DateTimeOffset.UtcNow)));
            }

            await using (var store = CreateStore(directory))
            {
                await store.InitializeAsync();
                (await store.GetMailboxStateAsync(accountId))!.LastImportedRevision.Should().Be(1);
                (await store.GetCurrentDocumentsAsync(accountId, ["m1"])).Should().ContainKey("m1");
                (await store.GetCurrentDocumentsAsync(accountId)).Should().ContainSingle(document =>
                    document.ServerMessageKey == "m1");

                await store.ApplyChangesAsync(accountId, mailboxId, Page(epoch, 2, 2,
                    new IntelligenceDocumentChangeDto(2, "m1", true, null, DateTimeOffset.UtcNow)));

                (await store.GetCurrentDocumentsAsync(accountId, ["m1"])).Should().BeEmpty();
                (await store.GetCurrentDocumentsAsync(accountId)).Should().BeEmpty();
                (await store.GetMailboxStateAsync(accountId))!.LastImportedRevision.Should().Be(2);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidEmbedding_RollsBackDocumentAndCursor()
    {
        var directory = CreateTemporaryDirectory();
        var accountId = Guid.NewGuid();
        var mailboxId = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        try
        {
            await using var store = CreateStore(directory);
            await store.InitializeAsync();
            await store.AlignMailboxHeadAsync(accountId, Head(mailboxId, epoch, 1));
            var invalid = Document("m1", 1, embeddingDimensions: 767);

            var action = () => store.ApplyChangesAsync(accountId, mailboxId, Page(epoch, 1, 1,
                new IntelligenceDocumentChangeDto(1, "m1", false, invalid, DateTimeOffset.UtcNow)));

            await action.Should().ThrowAsync<InvalidOperationException>();
            (await store.GetMailboxStateAsync(accountId))!.LastImportedRevision.Should().Be(0);
            (await store.GetCurrentDocumentsAsync(accountId, ["m1"])).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EpochChange_ClearsOnlyDocumentsAndPreservesMailboxPreferences()
    {
        var directory = CreateTemporaryDirectory();
        var accountId = Guid.NewGuid();
        var mailboxId = Guid.NewGuid();
        var firstEpoch = Guid.NewGuid();
        var secondEpoch = Guid.NewGuid();
        try
        {
            await using var store = CreateStore(directory);
            await store.InitializeAsync();
            await store.AlignMailboxHeadAsync(accountId, Head(mailboxId, firstEpoch, 1));
            await store.SetHeadlineLanguageAsync(accountId, mailboxId, "tr-TR");
            await store.ApplyChangesAsync(accountId, mailboxId, Page(firstEpoch, 1, 1,
                new IntelligenceDocumentChangeDto(1, "m1", false, Document("m1", 1), DateTimeOffset.UtcNow)));

            await store.AlignMailboxHeadAsync(accountId, Head(mailboxId, secondEpoch, 0));

            var state = await store.GetMailboxStateAsync(accountId);
            state!.IndexEpoch.Should().Be(secondEpoch);
            state.LastImportedRevision.Should().Be(0);
            (await store.GetCurrentDocumentsAsync(accountId, ["m1"])).Should().BeEmpty();
            (await store.GetHeadlineLanguageAsync(accountId)).Should().Be("tr-TR");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Initialize_MigratesLegacyMailboxStateColumns()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "WinoIntelligence.db");
            var connection = new SQLiteAsyncConnection(path);
            await connection.ExecuteAsync("CREATE TABLE LocalMailboxState (LocalAccountId TEXT PRIMARY KEY NOT NULL, MailboxId TEXT NOT NULL, LastImportedRevision INTEGER NOT NULL, UpdatedAtUtc TEXT NOT NULL)");
            await connection.CloseAsync();

            await using var store = CreateStore(directory);
            await store.InitializeAsync();
            var accountId = Guid.NewGuid();
            var head = Head(Guid.NewGuid(), Guid.NewGuid(), 0);
            await store.AlignMailboxHeadAsync(accountId, head);

            var state = await store.GetMailboxStateAsync(accountId);
            state!.IntelligenceVersion.Should().Be(WinoIntelligenceVersions.V1);
            state.IndexEpoch.Should().Be(head.IndexEpoch);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static LocalIntelligenceStore CreateStore(string directory)
    {
        var configuration = new Mock<IApplicationConfiguration>();
        configuration.SetupGet(value => value.ApplicationDataFolderPath).Returns(directory);
        return new LocalIntelligenceStore(configuration.Object, new StrongReferenceMessenger());
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wino-intelligence-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static MailboxIntelligenceHeadDto Head(Guid mailboxId, Guid epoch, long revision)
    {
        var now = DateTimeOffset.UtcNow;
        return new(
            mailboxId,
            WinoIntelligenceVersions.V1,
            epoch,
            revision,
            revision,
            revision * 3_072,
            revision == 0 ? null : now,
            revision == 0 ? null : now,
            now,
            now);
    }

    private static IntelligenceChangesPageDto Page(
        Guid epoch,
        long throughRevision,
        long nextAfterRevision,
        params IntelligenceDocumentChangeDto[] items)
        => new(WinoIntelligenceVersions.V1, epoch, throughRevision, nextAfterRevision, items);

    private static MessageIntelligenceDownloadDto Document(string key, long revision, int embeddingDimensions = 768)
        => new()
        {
            ServerMessageKey = key,
            ContentHash = $"hash-{key}",
            Subject = key,
            Sender = "sender@example.test",
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            IsOutgoing = false,
            IsRead = true,
            IsFlagged = false,
            HasAttachments = false,
            FolderIds = ["inbox"],
            SenderAddresses = ["sender@example.test"],
            RecipientAddresses = ["user@example.test"],
            Analysis = new MessageIntelligenceDocumentV1
            {
                SourceLanguage = "en",
                Headline = key,
                Summary = key,
                Category = MessageCategoryV1.Conversation,
                Intent = MessageIntentV1.Inform,
                Urgency = MessageUrgencyV1.Normal,
                Confidence = 1,
            },
            Embedding = Convert.ToBase64String(new byte[3_072]),
            EmbeddingDimensions = embeddingDimensions,
            EmbeddingEncoding = "float32-le",
            ArtifactRevision = revision,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
        };
}
