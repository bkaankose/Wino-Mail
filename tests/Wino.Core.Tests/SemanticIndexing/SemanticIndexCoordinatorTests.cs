using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.AI.Cryptography;
using Wino.Mail.Contracts.Intelligence;
using Wino.Mail.Contracts.SemanticIndex;
using Wino.Services;
using Wino.Core.Tests.Helpers;
using Wino.Messaging.UI;
using Xunit;

namespace Wino.Core.Tests.SemanticIndexing;

public sealed class SemanticIndexCoordinatorTests
{
    private const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEApwRDYSXuiybl8qzU8mTi
        uRuYRZrh/+9F3y6ncZ9KGUcs9ht1leqYfd6gecG4/lawB3LsRWNYco/qswpkcFb/
        Wlf+em4bdu1nDykRnPrHv+x1Dn8LqnIQZ1M1+OMP7+2db7qw8EUuBWJ00LSJ/q58
        I1O1jjstUtxRVj3P1Ei3Goau5IhzVfhSZAlApRXM6DvX/6exVQoKI/F9KXX5tAVV
        Bev4tsQ19Fz6O9QcJh8wm6zYL3vC3CT7e1LtmR/PEQIrgcIgZOJk+6LShbatIuYP
        1mBReiMbUqpoUOaooN/Qi+0y6HOqlOLEPODdiY6Qd9qsGGnw/MWX+z1pP/bqfOOs
        gO0tb2OTNEtQ4kPOIVDL+wzLYcbcnbbNjlQ5UUATHpjNBgPj+u+eaTRLqpdrl4O3
        xYVnA4mVKpYqMgCrvHkPETvcNW9fjU7pA7hiubikht3zorV5o+NyblFXPR8K+oiy
        egIlAcMu0ngv3/x9EaRE1o6VfSq7rantZU0zMGp2j84xAgMBAAE=
        -----END PUBLIC KEY-----
        """;

    [Fact]
    public void CalculatingSnapshot_RemainsActiveWhileReconciliationIsRunning()
    {
        var snapshot = new SemanticIndexJobSnapshot(
            Guid.NewGuid(),
            SemanticIndexJobStatus.Calculating,
            UploadedMessageCount: 0,
            SelectedMessageCount: 100);

        snapshot.IsActive.Should().BeTrue();
        snapshot.ProcessedMessageCount.Should().Be(0);
        snapshot.SucceededMessageCount.Should().Be(0);
    }

    [Fact]
    public async Task CoveredSelection_IsImportedWithoutCandidateResolutionOrUpload()
    {
        await using var fixture = await CreateFixtureAsync(["covered"]);

        await fixture.Coordinator.StartIndexingAsync(
            fixture.Account.Id,
            ["covered", "covered"]);
        await WaitForCompletionAsync(fixture.Coordinator, fixture.Account.Id);

        var snapshot = fixture.Coordinator.GetJobSnapshot(fixture.Account.Id);
        snapshot.Status.Should().Be(SemanticIndexJobStatus.Completed);
        snapshot.SelectedMessageCount.Should().Be(1);
        snapshot.RestoredMessageCount.Should().Be(1);
        fixture.MessageResolver.Verify(
            resolver => resolver.GetCandidatesAsync(
                It.IsAny<Guid>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        fixture.Backend.Verify(
            backend => backend.IngestAsync(
                It.IsAny<Guid>(),
                It.IsAny<byte[]>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        fixture.LocalStore.Verify(
            store => store.UpsertArtifactsAsync(
                fixture.Account.Id,
                fixture.MailboxId,
                It.IsAny<IReadOnlyList<IntelligenceArtifactDto>>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Download_ReconcilesAllLocalKeysAndNeverUploads()
    {
        await using var fixture = await CreateFixtureAsync(["covered"]);
        fixture.MessageResolver.Setup(resolver => resolver.GetCandidatesAsync(
                fixture.Account.Id,
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                Candidate("covered"),
                Candidate("missing"),
            ]);

        var result = await fixture.Coordinator.DownloadAvailableIntelligenceAsync(fixture.Account.Id);

        result.CoveredRemoteMessageIds.Should().BeEquivalentTo(["covered"]);
        fixture.ApiClient.Verify(
            client => client.ReconcileIntelligenceAsync(
                fixture.MailboxId,
                It.IsAny<byte[]>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        fixture.Backend.Verify(
            backend => backend.IngestAsync(
                It.IsAny<Guid>(),
                It.IsAny<byte[]>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        fixture.MessageResolver.Verify(
            resolver => resolver.GetContentAsync(
                It.IsAny<Guid>(),
                It.IsAny<IntelligenceMessageCandidate>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// A read-only restore is not a job. It used to publish a Calculating snapshot covering every
    /// message in the account and then return without ever publishing a terminal one, so the page
    /// showed a second, much larger indexing run that never finished.
    /// </summary>
    [Fact]
    public async Task DownloadAvailableIntelligence_PublishesNoJobSnapshots()
    {
        await using var fixture = await CreateFixtureAsync(["covered"]);
        fixture.MessageResolver
            .Setup(resolver => resolver.GetCandidatesAsync(
                fixture.Account.Id, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Candidate("covered"), Candidate("other")]);

        var published = new List<SemanticIndexJobSnapshot>();
        var recipient = new object();
        fixture.Messenger.Register<SemanticIndexJobChanged>(
            recipient, (_, message) => published.Add(message.Snapshot));

        await fixture.Coordinator.DownloadAvailableIntelligenceAsync(fixture.Account.Id);

        published.Should().BeEmpty();
        fixture.Coordinator.GetJobSnapshot(fixture.Account.Id).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task DownloadAvailableIntelligence_LeavesNoActiveJobBehindForTheNextIndexingRun()
    {
        await using var fixture = await CreateFixtureAsync(["covered"]);
        fixture.MessageResolver
            .Setup(resolver => resolver.GetCandidatesAsync(
                fixture.Account.Id, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Candidate("covered")]);

        await fixture.Coordinator.DownloadAvailableIntelligenceAsync(fixture.Account.Id);

        var snapshot = fixture.Coordinator.GetJobSnapshot(fixture.Account.Id);
        snapshot.Status.Should().NotBe(SemanticIndexJobStatus.Calculating);
        snapshot.SelectedMessageCount.Should().Be(0);
    }

    /// <summary>
    /// Manual, single-message processing must not be blocked by a batch run. Someone reading a
    /// message and pressing "process" used to get "indexing is already in progress" whenever a
    /// mailbox-wide job happened to hold the job registry lock.
    /// </summary>
    [Fact]
    public async Task IndexMessage_SucceedsWhileTheJobRegistryIsHeldByABatchRun()
    {
        await using var fixture = await CreateFixtureAsync(["covered"]);
        fixture.MessageResolver
            .Setup(resolver => resolver.FindCandidateAsync(
                fixture.Account.Id, "unique-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Candidate("covered"));

        // Hold the registry the way a running batch job would.
        var release = new TaskCompletionSource();
        fixture.Registry.TryStart(fixture.Account.Id, _ => release.Task, out _)
            .Should().BeTrue();

        try
        {
            await fixture.Coordinator.IndexMessageAsync(fixture.Account.Id, "unique-1");
        }
        finally
        {
            release.SetResult();
        }

        (await fixture.Coordinator.GetMessageStateAsync(fixture.Account.Id, "unique-1"))
            .Should().Be(SemanticMessageIndexState.Indexed);
    }

    /// <summary>
    /// A one-message run has no progress worth showing, and publishing snapshots would overwrite
    /// the progress of whatever batch job is running alongside it.
    /// </summary>
    [Fact]
    public async Task IndexMessage_PublishesNoJobSnapshots()
    {
        await using var fixture = await CreateFixtureAsync(["covered"]);
        fixture.MessageResolver
            .Setup(resolver => resolver.FindCandidateAsync(
                fixture.Account.Id, "unique-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Candidate("covered"));

        var published = new List<SemanticIndexJobSnapshot>();
        var recipient = new object();
        fixture.Messenger.Register<SemanticIndexJobChanged>(
            recipient, (_, message) => published.Add(message.Snapshot));

        await fixture.Coordinator.IndexMessageAsync(fixture.Account.Id, "unique-1");

        published.Should().BeEmpty();
        fixture.Coordinator.GetJobSnapshot(fixture.Account.Id).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task IndexMessage_RestoresFromTheCloudWithoutIngestingAgain()
    {
        await using var fixture = await CreateFixtureAsync(["covered"]);
        fixture.MessageResolver
            .Setup(resolver => resolver.FindCandidateAsync(
                fixture.Account.Id, "unique-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Candidate("covered"));

        await fixture.Coordinator.IndexMessageAsync(fixture.Account.Id, "unique-1");

        fixture.Backend.Verify(
            backend => backend.IngestAsync(
                It.IsAny<Guid>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
        fixture.MessageResolver.Verify(
            resolver => resolver.GetContentAsync(
                It.IsAny<Guid>(),
                It.IsAny<IntelligenceMessageCandidate>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static async Task<Fixture> CreateFixtureAsync(IReadOnlyList<string> coveredIds)
    {
        var mailboxId = Guid.NewGuid();
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Address = "mail@example.test",
            ProviderType = MailProviderType.Outlook,
            Preferences = new MailAccountPreferences
            {
                IsSemanticIndexingEnabled = true,
            },
        };
        var accountService = new Mock<IAccountService>();
        accountService.Setup(service => service.GetAccountAsync(account.Id)).ReturnsAsync(account);
        var apiClient = new Mock<IWinoAccountApiClient>();
        apiClient.Setup(client => client.GetSemanticMailboxesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SemanticMailboxDto(
                mailboxId,
                account.Address,
                (int)account.ProviderType,
                null)]);
        apiClient.Setup(client => client.ReconcileIntelligenceAsync(
                mailboxId,
                It.IsAny<byte[]>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntelligenceReconciliationResultDto(
                coveredIds,
                [],
                [DeletionMarker(coveredIds[0])]));
        var localStore = new Mock<ILocalIntelligenceStore>();
        // The real store returns an empty list, never null, for a message it has nothing for.
        localStore
            .Setup(store => store.GetCurrentArtifactsAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var messageResolver = new Mock<IIntelligenceMessageContextResolver>();
        var backend = new Mock<IIntelligenceBackend>();
        var database = new InMemoryDatabaseService();
        await database.InitializeAsync();
        await database.Connection.InsertAsync(new WinoAccount
        {
            Id = Guid.NewGuid(),
        });
        var encryptor = new PemContentEnvelopeEncryptor(new ContentEncryptionPublicKey(
            "wino-intelligence-2026-08-v1",
            PublicKey));
        var messenger = new StrongReferenceMessenger();
        var registry = new SemanticIndexJobRegistry();
        var coordinator = new SemanticIndexCoordinator(
            database,
            accountService.Object,
            apiClient.Object,
            localStore.Object,
            Mock.Of<ILocalIntelligenceService>(),
            encryptor,
            registry,
            Mock.Of<ITranslationService>(),
            messageResolver.Object,
            backend.Object,
            messenger);

        return new Fixture(
            coordinator,
            account,
            mailboxId,
            apiClient,
            localStore,
            messageResolver,
            backend,
            database,
            messenger,
            registry);
    }

    private static async Task WaitForCompletionAsync(
        ISemanticIndexCoordinator coordinator,
        Guid accountId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (coordinator.GetJobSnapshot(accountId).IsActive)
            await Task.Delay(10, timeout.Token);
    }

    private static IntelligenceArtifactDto DeletionMarker(string remoteMessageId)
        => new()
        {
            RemoteMessageId = remoteMessageId,
            Capability = IntelligenceCapability.SmartLabels,
            GenerationVersion = 2,
            PayloadSchemaVersion = 1,
            ArtifactRevision = 1,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            ContentHash = string.Empty,
            IsDeleted = true,
        };

    private static IntelligenceMessageCandidate Candidate(string remoteMessageId)
        => new(
            Guid.NewGuid(),
            remoteMessageId,
            remoteMessageId,
            [],
            string.Empty,
            string.Empty,
            DateTime.UtcNow,
            null,
            false,
            false,
            "normal",
            ["inbox"],
            new MailBodyLocator(remoteMessageId, "inbox", 0, 0, remoteMessageId));

    private sealed record Fixture(
        SemanticIndexCoordinator Coordinator,
        MailAccount Account,
        Guid MailboxId,
        Mock<IWinoAccountApiClient> ApiClient,
        Mock<ILocalIntelligenceStore> LocalStore,
        Mock<IIntelligenceMessageContextResolver> MessageResolver,
        Mock<IIntelligenceBackend> Backend,
        InMemoryDatabaseService Database,
        StrongReferenceMessenger Messenger,
        SemanticIndexJobRegistry Registry) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
