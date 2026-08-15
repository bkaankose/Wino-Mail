using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.Contracts.Intelligence;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.SemanticIndexing;

public sealed class LocalIntelligenceStoreRecoveryTests
{
    [Fact]
    public async Task BriefingFactAndAccessSnapshot_PersistAndViewedStateClearsIndicator()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"wino-intelligence-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var configuration = new TestConfiguration(folder);
            var localAccountId = Guid.NewGuid();
            var winoAccountId = Guid.NewGuid();
            var mailboxId = Guid.NewGuid();
            await using (var store = new LocalIntelligenceStore(configuration))
            {
                await store.SaveAccessSnapshotAsync(new(localAccountId, winoAccountId, true, true,
                    mailboxId, DateTimeOffset.UtcNow));
                await store.ImportAsync(localAccountId, mailboxId,
                    [CreateBriefingFact("briefing-message", 9, replyRequired: true)], throughRevision: 9);
                (await store.GetDailyBriefingUnseenStateAsync([localAccountId])).HasUnseenContent.Should().BeTrue();
                await store.MarkDailyBriefingViewedAsync([localAccountId], DateTimeOffset.UtcNow.AddSeconds(1));
                (await store.GetDailyBriefingUnseenStateAsync([localAccountId])).HasUnseenContent.Should().BeFalse();
            }

            await using var reopened = new LocalIntelligenceStore(configuration);
            var access = await reopened.GetAccessSnapshotAsync(localAccountId);
            access.Should().NotBeNull();
            access!.IsEligible.Should().BeTrue();
            access.MailboxId.Should().Be(mailboxId);
            (await reopened.GetDailyBriefingUnseenStateAsync([localAccountId])).HasUnseenContent.Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task JobIntent_PersistsAcrossStoreRestart_AndIsRemovedWithLocalMailbox()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"wino-intelligence-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var configuration = new TestConfiguration(folder);
            var accountId = Guid.NewGuid();
            var mailboxId = Guid.NewGuid();
            var cutoff = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            var through = DateTimeOffset.Parse("2026-02-01T00:00:00Z");
            await using (var store = new LocalIntelligenceStore(configuration))
            {
                await store.SaveJobIntentAsync(new SemanticIndexJobIntent(
                    accountId,
                    mailboxId,
                    SemanticIndexRangePreset.SixMonths,
                    cutoff,
                    through,
                    true,
                    "in-progress",
                    DateTimeOffset.UtcNow));
            }

            await using (var reopened = new LocalIntelligenceStore(configuration))
            {
                var restored = (await reopened.GetJobIntentsAsync()).Single();
                restored.LocalAccountId.Should().Be(accountId);
                restored.ServerMailboxId.Should().Be(mailboxId);
                restored.RangePreset.Should().Be(SemanticIndexRangePreset.SixMonths);
                restored.CutoffUtc.Should().Be(cutoff);
                restored.ThroughUtcExclusive.Should().Be(through);
                restored.AutomaticallyIndexNewMessages.Should().BeTrue();
                restored.BackfillStatus.Should().Be("in-progress");
                await reopened.DeleteMailboxAsync(accountId);
                (await reopened.GetJobIntentsAsync()).Should().BeEmpty();
            }
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task PreparedDocument_IsProtectedAcrossRestart_AndDeletedAfterConfirmation()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"wino-intelligence-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var configuration = new TestConfiguration(folder);
            var accountId = Guid.NewGuid();
            const string remoteMessageId = "outlook:staged-message";
            const string sensitiveContent = "private message body that must not appear in sqlite";
            var document = new IntelligenceIndexDocumentRequest
            {
                ClientCorrelationId = Guid.NewGuid(),
                ProviderMessageId = "provider-message",
                ContentHash = "content-hash",
                CanonicalContent = sensitiveContent,
                OccurredAtUtc = DateTimeOffset.Parse("2026-08-12T10:00:00Z"),
                SenderAddresses = ["sender@example.com"],
                SenderDomains = ["example.com"],
                ProviderFolderIds = ["inbox"],
            };

            await using (var store = new LocalIntelligenceStore(configuration))
                await store.SavePreparedDocumentAsync(accountId, remoteMessageId, document);

            var needle = Encoding.UTF8.GetBytes(sensitiveContent);
            foreach (var file in Directory.GetFiles(folder, "WinoIntelligence.db*"))
                ContainsSequence(File.ReadAllBytes(file), needle).Should().BeFalse();

            await using (var reopened = new LocalIntelligenceStore(configuration))
            {
                var restored = await reopened.GetPreparedDocumentAsync(accountId, remoteMessageId);
                restored.Should().NotBeNull();
                restored!.CanonicalContent.Should().Be(sensitiveContent);
                restored.ContentHash.Should().Be(document.ContentHash);

                await reopened.DeletePreparedDocumentsAsync(accountId, [remoteMessageId]);
                (await reopened.GetPreparedDocumentAsync(accountId, remoteMessageId)).Should().BeNull();
            }
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task TypedCapabilityArtifact_PersistsAcrossStoreRestart()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"wino-intelligence-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var configuration = new TestConfiguration(folder);
            var accountId = Guid.NewGuid();
            var mailboxId = Guid.NewGuid();
            var generatedAt = DateTimeOffset.Parse("2026-08-12T10:00:00Z");
            var briefingId = Guid.NewGuid();
            var artifact = new IntelligenceArtifactDto
            {
                RemoteMessageId = "server-message-key",
                ContentHash = "content-hash",
                Capability = IntelligenceCapability.BriefingFact,
                GenerationVersion = 1,
                PayloadSchemaVersion = 2,
                ArtifactRevision = 7,
                GeneratedAtUtc = generatedAt,
                IsDeleted = false,
                Confidence = 0.94,
                BriefingFact = new FinanceFactPayload
                {
                    BriefingId = briefingId,
                    OccurredAtUtc = generatedAt,
                    Kind = MessageKind.Invoice,
                    Status = BriefingStatus.ActionRequired,
                    Urgency = MailPriority.High,
                    PrimaryAction = new PayActionPayload { Confidence = 0.94 },
                    TemporalReferences =
                    [
                        new DateRangeTemporalPayload
                        {
                            Start = new TemporalPointPayload(new DateOnly(2026, 8, 15), null, null, "UTC", 0, TemporalPrecision.Date),
                            End = new TemporalPointPayload(new DateOnly(2026, 8, 17), null, null, "UTC", 0, TemporalPrecision.Date),
                            Confidence = 0.94,
                        },
                    ],
                    Confidence = 0.94,
                },
            };
            var serialized = LocalIntelligenceStore.SerializeStoredArtifact(artifact);
            serialized.Should().Contain("\"category\":\"finance\"");
            LocalIntelligenceStore.DeserializeTypedArtifact(serialized).BriefingFact.Should().BeOfType<FinanceFactPayload>();

            await using (var store = new LocalIntelligenceStore(configuration))
                await store.ImportAsync(accountId, mailboxId, [artifact], throughRevision: 7);

            await using var reopened = new LocalIntelligenceStore(configuration);
            var restored = (await reopened.GetCurrentArtifactsAsync(accountId, "server-message-key")).Single();
            restored.Capability.Should().Be(IntelligenceCapability.BriefingFact);
            var restoredFact = restored.BriefingFact.Should().BeOfType<FinanceFactPayload>().Subject;
            restoredFact.BriefingId.Should().Be(briefingId);
            restoredFact.PrimaryAction.Should().BeOfType<PayActionPayload>();
            restoredFact.TemporalReferences.Should().ContainSingle().Which.Should().BeOfType<DateRangeTemporalPayload>();
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task BatchedCurrentArtifacts_SelectsLatestRevisionAndIsolatesAccounts()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"wino-intelligence-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var configuration = new TestConfiguration(folder);
            var accountId = Guid.NewGuid();
            var otherAccountId = Guid.NewGuid();
            var mailboxId = Guid.NewGuid();
            await using var store = new LocalIntelligenceStore(configuration);
            await store.ImportAsync(accountId, mailboxId,
            [
                CreateBriefingFact("outlook:one", revision: 1, replyRequired: false),
                CreateBriefingFact("outlook:one", revision: 2, replyRequired: true),
                CreateBriefingFact("outlook:two", revision: 3, replyRequired: true),
                CreateBriefingFact("outlook:deleted", revision: 4, replyRequired: true),
                CreateBriefingFact("outlook:deleted", revision: 5, replyRequired: false, isDeleted: true),
            ], throughRevision: 5);
            await store.ImportAsync(otherAccountId, Guid.NewGuid(),
            [
                CreateBriefingFact("outlook:one", revision: 4, replyRequired: false),
            ], throughRevision: 4);

            var result = await store.GetCurrentArtifactsAsync(accountId, ["outlook:one", "outlook:two", "outlook:deleted"]);

            result.Should().HaveCount(3);
            result["outlook:one"].Single().ArtifactRevision.Should().Be(2);
            result["outlook:one"].Single().BriefingFact!.PrimaryAction.Should().BeOfType<ReplyActionPayload>();
            result["outlook:two"].Single().BriefingFact!.PrimaryAction.Should().BeOfType<ReplyActionPayload>();
            result["outlook:deleted"].Single().IsDeleted.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task CompletedMessages_IncludeSeparateHeadlineRowsAndRemoveDeletedHeadlines()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"wino-intelligence-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var accountId = Guid.NewGuid();
            var mailboxId = Guid.NewGuid();
            var briefingId = Guid.NewGuid();
            await using var store = new LocalIntelligenceStore(new TestConfiguration(folder));
            await store.ImportAsync(accountId, mailboxId,
            [
                new IntelligenceArtifactDto
                {
                    RemoteMessageId = "outlook:complete", ContentHash = "hash",
                    Capability = IntelligenceCapability.SmartLabels, GenerationVersion = 1,
                    PayloadSchemaVersion = 1, ArtifactRevision = 1, GeneratedAtUtc = DateTimeOffset.UtcNow,
                    SmartLabels = new SmartLabelsCapabilityPayload([]),
                },
                new IntelligenceArtifactDto
                {
                    RemoteMessageId = "outlook:complete", ContentHash = "hash",
                    Capability = IntelligenceCapability.BriefingFact, GenerationVersion = 1,
                    PayloadSchemaVersion = 2, ArtifactRevision = 2, GeneratedAtUtc = DateTimeOffset.UtcNow,
                    BriefingFact = new ConversationFactPayload
                    {
                        BriefingId = briefingId, OccurredAtUtc = DateTimeOffset.UtcNow,
                        Kind = MessageKind.Conversation, Status = BriefingStatus.Informational,
                        Urgency = MailPriority.Normal, PrimaryAction = new NoActionPayload(),
                        TemporalReferences = [], Confidence = 0.9,
                    },
                },
                new IntelligenceArtifactDto
                {
                    RemoteMessageId = "outlook:complete", ContentHash = "hash",
                    Capability = IntelligenceCapability.BriefingHeadline, GenerationVersion = 1,
                    PayloadSchemaVersion = 1, ArtifactRevision = 3, GeneratedAtUtc = DateTimeOffset.UtcNow,
                    BriefingHeadline = new BriefingHeadlineCapabilityPayload(briefingId, "A short headline"),
                },
            ], throughRevision: 3);
            var capabilities = new[]
            {
                Capability(IntelligenceCapability.SmartLabels, 1, 1),
                Capability(IntelligenceCapability.BriefingFact, 1, 2),
                Capability(IntelligenceCapability.BriefingHeadline, 1, 1),
            };

            (await store.GetCompletedMessageIdsAsync(accountId, capabilities))
                .Should().ContainSingle().Which.Should().Be("outlook:complete");

            await store.ImportAsync(accountId, mailboxId,
            [
                new IntelligenceArtifactDto
                {
                    RemoteMessageId = "outlook:complete", ContentHash = "hash",
                    Capability = IntelligenceCapability.BriefingHeadline, GenerationVersion = 1,
                    PayloadSchemaVersion = 1, ArtifactRevision = 4, GeneratedAtUtc = DateTimeOffset.UtcNow,
                    IsDeleted = true,
                },
            ], throughRevision: 4);

            (await store.GetCompletedMessageIdsAsync(accountId, capabilities)).Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteDatabase_RemovesDatabaseAndSidecarsWithoutRecreatingIt()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"wino-intelligence-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var configuration = new TestConfiguration(folder);
            await using var store = new LocalIntelligenceStore(configuration);
            await store.InitializeAsync();
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
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    private static IntelligenceArtifactDto CreateBriefingFact(string remoteMessageId, long revision, bool replyRequired, bool isDeleted = false)
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
                Status = replyRequired ? BriefingStatus.AwaitingMyReply : BriefingStatus.Informational,
                Urgency = MailPriority.Normal,
                PrimaryAction = replyRequired ? new ReplyActionPayload { Confidence = 0.9 } : new NoActionPayload { Confidence = 0.9 },
                TemporalReferences = [],
                Confidence = 0.9,
            },
        };

    private static IntelligenceCapabilityDto Capability(IntelligenceCapability capability, int generationVersion, int schemaVersion)
        => new()
        {
            Capability = capability,
            GenerationVersion = generationVersion,
            PayloadSchemaVersion = schemaVersion,
            RequiresContent = true,
            Trigger = IntelligenceCapabilityTrigger.Synchronization,
        };

    private static bool ContainsSequence(byte[] source, byte[] value)
    {
        for (var index = 0; index <= source.Length - value.Length; index++)
        {
            if (source.AsSpan(index, value.Length).SequenceEqual(value))
                return true;
        }
        return false;
    }

    private sealed class TestConfiguration(string folder) : IApplicationConfiguration
    {
        public string ApplicationDataFolderPath { get; set; } = folder;
        public string PublisherSharedFolderPath { get; set; } = folder;
        public string ApplicationTempFolderPath { get; set; } = folder;
        public string SentryDNS => string.Empty;
    }
}
