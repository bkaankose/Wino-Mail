using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.Contracts.Intelligence;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.SemanticIndexing;

public sealed class LocalIntelligenceStoreRecoveryTests
{
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
            var artifact = new IntelligenceArtifactDto
            {
                RemoteMessageId = "server-message-key",
                ContentHash = "content-hash",
                Capability = IntelligenceCapability.Deadline,
                GenerationVersion = 1,
                PayloadSchemaVersion = 1,
                ArtifactRevision = 7,
                GeneratedAtUtc = generatedAt,
                IsDeleted = false,
                Confidence = 0.94,
                Deadline = new DeadlineCapabilityPayload(
                    true,
                    DeadlineKind.Payment,
                    DateTimeOffset.Parse("2026-08-15T10:00:00Z"),
                    new DateOnly(2026, 8, 15),
                    "UTC",
                    DeadlinePrecision.DateTime,
                    DeadlineAction.Pay,
                    0.94,
                    new DateOnly(2026, 8, 17)),
            };
            var serialized = LocalIntelligenceStore.SerializeStoredArtifact(artifact);
            serialized.Should().Contain("\"deadlineKind\":\"payment\"");
            LocalIntelligenceStore.DeserializeTypedArtifact(serialized).Deadline!.Kind.Should().Be(DeadlineKind.Payment);

            await using (var store = new LocalIntelligenceStore(configuration))
                await store.ImportAsync(accountId, mailboxId, [artifact], throughRevision: 7);

            await using var reopened = new LocalIntelligenceStore(configuration);
            var restored = (await reopened.GetCurrentArtifactsAsync(accountId, "server-message-key")).Single();
            restored.Capability.Should().Be(IntelligenceCapability.Deadline);
            restored.Deadline.Should().NotBeNull();
            restored.Deadline!.Kind.Should().Be(DeadlineKind.Payment);
            restored.Deadline.LocalDateEnd.Should().Be(new DateOnly(2026, 8, 17));
            restored.Deadline.Action.Should().Be(DeadlineAction.Pay);
            restored.Deadline.DueAtUtc.Should().Be(DateTimeOffset.Parse("2026-08-15T10:00:00Z"));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

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
