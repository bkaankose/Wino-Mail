using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using SQLite;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.Contracts.Intelligence;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.SemanticIndexing;

public sealed class LocalIntelligenceStoreRecoveryTests
{
    [Fact]
    public async Task OperationBeforeInitialization_FailsClearly()
    {
        var folder = CreateTemporaryFolder();

        try
        {
            await using var store = CreateStore(folder);
            var operation = () => store.GetCurrentArtifactsAsync(Guid.NewGuid(), "message");

            await operation.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("The local intelligence store has not been initialized.");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent_AndDoesNotCreateRemovedTables()
    {
        var folder = CreateTemporaryFolder();

        try
        {
            await using var store = CreateStore(folder);
            await Task.WhenAll(store.InitializeAsync(), store.InitializeAsync(), store.InitializeAsync());

            var connection = new SQLiteAsyncConnection(Path.Combine(folder, "WinoIntelligence.db"));
            var tables = await connection.QueryAsync<TableNameRow>(
                "SELECT name FROM sqlite_master WHERE type = 'table'");

            tables.Select(static row => row.Name).Should().NotContain([
                "LocalIndexJob",
                "LocalPreparedDocument",
            ]);

            await connection.CloseAsync();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ArtifactUpsert_PersistsLatestRevisionAndDeletionMarker()
    {
        var folder = CreateTemporaryFolder();

        try
        {
            var accountId = Guid.NewGuid();
            var mailboxId = Guid.NewGuid();
            await using (var store = await CreateInitializedStoreAsync(folder))
            {
                await store.UpsertArtifactsAsync(accountId, mailboxId,
                [
                    CreateBriefingFact("message", revision: 1, isDeleted: false),
                    CreateBriefingFact("message", revision: 2, isDeleted: true),
                ]);
            }

            await using var reopened = await CreateInitializedStoreAsync(folder);
            var artifact = (await reopened.GetCurrentArtifactsAsync(accountId, "message")).Single();

            artifact.ArtifactRevision.Should().Be(2);
            artifact.IsDeleted.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task AccessAndBriefingState_PersistAndAreRemovedWithMailbox()
    {
        var folder = CreateTemporaryFolder();

        try
        {
            var accountId = Guid.NewGuid();
            var mailboxId = Guid.NewGuid();
            var briefingId = Guid.NewGuid();
            var ignoredAt = DateTimeOffset.Parse("2026-08-16T10:00:00Z");
            await using (var store = await CreateInitializedStoreAsync(folder))
            {
                await store.SaveAccessSnapshotAsync(new(
                    accountId,
                    Guid.NewGuid(),
                    true,
                    true,
                    mailboxId,
                    DateTimeOffset.UtcNow));
                await store.SaveDailyBriefingIgnoreAsync(accountId, briefingId, 12, ignoredAt);
                await store.UpsertArtifactsAsync(
                    accountId,
                    mailboxId,
                    [CreateBriefingFact("briefing-message", revision: 9, isDeleted: false)]);
            }

            await using var reopened = await CreateInitializedStoreAsync(folder);
            (await reopened.GetAccessSnapshotAsync(accountId)).Should().NotBeNull();
            (await reopened.GetDailyBriefingIgnoreRevisionsAsync(accountId))
                .Should().ContainSingle().Which.Should().Be(new KeyValuePair<Guid, long>(briefingId, 12));

            await reopened.DeleteMailboxAsync(accountId);

            (await reopened.GetCurrentArtifactsAsync(accountId, "briefing-message")).Should().BeEmpty();
            (await reopened.GetDailyBriefingIgnoreRevisionsAsync(accountId)).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteDatabase_RemovesDatabaseAndSidecarsWithoutRecreatingIt()
    {
        var folder = CreateTemporaryFolder();

        try
        {
            await using var store = await CreateInitializedStoreAsync(folder);
            var databasePath = Path.Combine(folder, "WinoIntelligence.db");
            await File.WriteAllTextAsync(databasePath + "-wal", "wal");
            await File.WriteAllTextAsync(databasePath + "-shm", "shm");

            await store.DeleteDatabaseAsync();

            store.DatabaseExists.Should().BeFalse();
            File.Exists(databasePath + "-wal").Should().BeFalse();
            File.Exists(databasePath + "-shm").Should().BeFalse();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static LocalIntelligenceStore CreateStore(string folder)
        => new(new TestConfiguration(folder), new StrongReferenceMessenger());

    private static async Task<LocalIntelligenceStore> CreateInitializedStoreAsync(string folder)
    {
        var store = CreateStore(folder);
        await store.InitializeAsync();
        return store;
    }

    private static string CreateTemporaryFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"wino-intelligence-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static IntelligenceArtifactDto CreateBriefingFact(
        string remoteMessageId,
        long revision,
        bool isDeleted)
        => new()
        {
            RemoteMessageId = remoteMessageId,
            ContentHash = "hash",
            Capability = IntelligenceCapability.BriefingFact,
            GenerationVersion = 1,
            PayloadSchemaVersion = 2,
            ArtifactRevision = revision,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            IsDeleted = isDeleted,
            BriefingFact = new ConversationFactPayload
            {
                BriefingId = Guid.NewGuid(),
                OccurredAtUtc = DateTimeOffset.UtcNow,
                Kind = MessageKind.Conversation,
                Status = BriefingStatus.Informational,
                Urgency = MailPriority.Normal,
                PrimaryAction = new NoActionPayload { Confidence = 0.9 },
                TemporalReferences = [],
                Confidence = 0.9,
            },
        };

    private sealed class TableNameRow
    {
        public string Name { get; set; } = string.Empty;
    }

    private sealed class TestConfiguration(string folder) : IApplicationConfiguration
    {
        public string ApplicationDataFolderPath { get; set; } = folder;
        public string PublisherSharedFolderPath { get; set; } = folder;
        public string ApplicationTempFolderPath { get; set; } = folder;
        public string SentryDNS => string.Empty;
    }
}
