using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
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
        fixture.ApiClient.Verify(
            client => client.StartMessageIntelligenceIngestionJobAsync(
                It.IsAny<Guid>(),
                It.IsAny<byte[]>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        fixture.LocalStore.Verify(
            store => store.ApplyChangesAsync(
                fixture.Account.Id,
                fixture.MailboxId,
                It.IsAny<IntelligenceChangesPageDto>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
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
            client => client.GetIntelligenceChangesAsync(
                fixture.MailboxId,
                WinoIntelligenceVersions.V1,
                It.IsAny<Guid>(),
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        fixture.ApiClient.Verify(
            client => client.StartMessageIntelligenceIngestionJobAsync(
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

        fixture.ApiClient.Verify(
            client => client.StartMessageIntelligenceIngestionJobAsync(
                It.IsAny<Guid>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
        fixture.MessageResolver.Verify(
            resolver => resolver.GetContentAsync(
                It.IsAny<Guid>(),
                It.IsAny<IntelligenceMessageCandidate>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EnsureMailbox_InitializesV1OnlyWhenHeadIsAbsent()
    {
        await using var fixture = await CreateFixtureAsync([], headInitiallyAbsent: true);

        await fixture.Coordinator.EnsureMailboxAsync(fixture.Account.Id);

        fixture.ApiClient.Verify(client => client.BeginIntelligenceReindexAsync(
            fixture.MailboxId,
            It.Is<BeginIntelligenceReindexRequest>(request =>
                request.TargetIntelligenceVersion == WinoIntelligenceVersions.V1 &&
                request.OperationId != Guid.Empty),
            It.IsAny<CancellationToken>()), Times.Once);
        fixture.ApiClient.Verify(client => client.GetIntelligenceStatusAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EnsureMailbox_ReusesExistingHeadWithoutObsoleteStatusCall()
    {
        await using var fixture = await CreateFixtureAsync([]);

        await fixture.Coordinator.EnsureMailboxAsync(fixture.Account.Id);

        fixture.ApiClient.Verify(client => client.BeginIntelligenceReindexAsync(
            It.IsAny<Guid>(), It.IsAny<BeginIntelligenceReindexRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        fixture.ApiClient.Verify(client => client.GetIntelligenceStatusAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task MissingSelection_ReconcilesBeforeEncryptedIngestAndImportsChangesAfterward()
    {
        await using var fixture = await CreateFixtureAsync([]);
        var candidate = Candidate("missing");
        fixture.MessageResolver.Setup(resolver => resolver.GetCandidatesAsync(
                fixture.Account.Id,
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([candidate]);
        fixture.MessageResolver.Setup(resolver => resolver.GetContentAsync(
                fixture.Account.Id,
                It.IsAny<IntelligenceMessageCandidate>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SemanticMailContent(
                new MailBodyContent(MailBodyFormat.Html, $"<script>{new string('x', 300_000)}</script><p>Message body</p>"),
                [new MailAddress("sender@example.test", "Sender")],
                [fixture.Account.Address],
                []));

        var calls = new List<string>();
        fixture.ApiClient.Setup(client => client.ReconcileMessageIntelligenceAsync(
                fixture.MailboxId,
                It.IsAny<ReconcileMessageIntelligenceRequest>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("reconcile"))
            .ReturnsAsync(new ReconcileMessageIntelligenceResultDto(
                WinoIntelligenceVersions.V1,
                fixture.Epoch,
                [],
                ["missing"]));
        fixture.ApiClient.Setup(client => client.GetIntelligenceChangesAsync(
                fixture.MailboxId,
                WinoIntelligenceVersions.V1,
                fixture.Epoch,
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("changes"))
            .ReturnsAsync(new IntelligenceChangesPageDto(
                WinoIntelligenceVersions.V1,
                fixture.Epoch,
                0,
                0,
                []));
        var jobId = Guid.NewGuid();
        fixture.ApiClient.Setup(client => client.StartMessageIntelligenceIngestionJobAsync(
                fixture.MailboxId,
                It.IsAny<byte[]>(),
                It.IsAny<CancellationToken>()))
            .Callback((Guid _, byte[] envelopeBytes, CancellationToken _) =>
            {
                calls.Add("start");
                var envelope = ContentEnvelopeBinaryCodec.Decode(envelopeBytes, 2 * 1024 * 1024);
                var route = $"/api/v1/ai/intelligence/mailboxes/{fixture.MailboxId:D}/ingestion-jobs";
                var plaintext = fixture.Decryptor.Decrypt(
                    envelope,
                    new ContentEnvelopeContext(fixture.WinoUserId, fixture.MailboxId, route));
                using var body = JsonDocument.Parse(plaintext);
                body.RootElement.GetProperty("IntelligenceVersion").GetString().Should().Be(WinoIntelligenceVersions.V1);
                body.RootElement.GetProperty("IndexEpoch").GetGuid().Should().Be(fixture.Epoch);
                var message = body.RootElement.GetProperty("Messages")[0];
                message.GetProperty("ServerMessageKey").GetString().Should().Be("missing");
                message.GetProperty("ContentHash").GetString().Should().MatchRegex("^[0-9a-f]{64}$");
                message.GetProperty("Subject").GetString().Should().BeEmpty();
                message.GetProperty("Sender").GetString().Should().Be("Sender <sender@example.test>");
                message.GetProperty("Body").GetString().Should().StartWith("Message body");
                message.GetProperty("Body").GetString().Should().NotContain("<p>");
                message.GetProperty("Body").GetString()!.Length.Should().BeLessThan(262_144);
                message.GetProperty("BodyIsHtml").GetBoolean().Should().BeFalse();
            })
            .ReturnsAsync(new MessageIntelligenceIngestionJobAcceptedDto(
                jobId,
                WinoIntelligenceVersions.V1,
                fixture.Epoch,
                1));
        var pollCount = 0;
        fixture.ApiClient.Setup(client => client.GetMessageIntelligenceIngestionJobAsync(
                fixture.MailboxId,
                jobId,
                It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("poll"))
            .ReturnsAsync(() => ++pollCount == 1
                ? new MessageIntelligenceIngestionJobDto(
                    jobId,
                    WinoIntelligenceVersions.V1,
                    fixture.Epoch,
                    MessageIntelligenceIngestionJobStatuses.Running,
                    1,
                    0,
                    0,
                    0,
                    0,
                    [])
                : new MessageIntelligenceIngestionJobDto(
                    jobId,
                    WinoIntelligenceVersions.V1,
                    fixture.Epoch,
                    MessageIntelligenceIngestionJobStatuses.Completed,
                    1,
                    1,
                    1,
                    0,
                    0,
                    [new MessageIntelligenceIngestItemDto("missing", MessageIntelligenceIngestionItemStatuses.Indexed, null, 1)]));

        await fixture.Coordinator.StartIndexingAsync(fixture.Account.Id, ["missing"]);
        await WaitForCompletionAsync(fixture.Coordinator, fixture.Account.Id);

        calls.Should().Equal("reconcile", "changes", "start", "poll", "poll", "changes");
        var snapshot = fixture.Coordinator.GetJobSnapshot(fixture.Account.Id);
        snapshot.Status.Should().Be(SemanticIndexJobStatus.Completed, snapshot.ErrorCode);
        snapshot.UploadedMessageCount.Should().Be(1);
        snapshot.FailedMessageCount.Should().Be(0);
    }

    [Fact]
    public async Task PartialIngestFailure_IsCountedWithoutMarkingTheWholeJobFailed()
    {
        await using var fixture = await CreateFixtureAsync([]);
        fixture.MessageResolver.Setup(resolver => resolver.GetCandidatesAsync(
                fixture.Account.Id,
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([Candidate("good"), Candidate("bad")]);
        fixture.MessageResolver.Setup(resolver => resolver.GetContentAsync(
                fixture.Account.Id,
                It.IsAny<IntelligenceMessageCandidate>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Content(fixture.Account.Address));
        var jobId = Guid.NewGuid();
        fixture.ApiClient.Setup(client => client.StartMessageIntelligenceIngestionJobAsync(
                fixture.MailboxId,
                It.IsAny<byte[]>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MessageIntelligenceIngestionJobAcceptedDto(
                jobId,
                WinoIntelligenceVersions.V1,
                fixture.Epoch,
                2));
        fixture.ApiClient.Setup(client => client.GetMessageIntelligenceIngestionJobAsync(
                fixture.MailboxId,
                jobId,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MessageIntelligenceIngestionJobDto(
                jobId,
                WinoIntelligenceVersions.V1,
                fixture.Epoch,
                MessageIntelligenceIngestionJobStatuses.Completed,
                2,
                2,
                1,
                0,
                1,
                [
                    new MessageIntelligenceIngestItemDto("good", MessageIntelligenceIngestionItemStatuses.Indexed, null, 1),
                    new MessageIntelligenceIngestItemDto("bad", MessageIntelligenceIngestionItemStatuses.Failed, "analysisFailed", null),
                ]));

        await fixture.Coordinator.StartIndexingAsync(fixture.Account.Id, ["good", "bad"]);
        await WaitForCompletionAsync(fixture.Coordinator, fixture.Account.Id);

        var snapshot = fixture.Coordinator.GetJobSnapshot(fixture.Account.Id);
        snapshot.Status.Should().Be(SemanticIndexJobStatus.Completed, snapshot.ErrorCode);
        snapshot.UploadedMessageCount.Should().Be(1);
        snapshot.FailedMessageCount.Should().Be(1);
    }

    private static async Task<Fixture> CreateFixtureAsync(
        IReadOnlyList<string> coveredIds,
        bool headInitiallyAbsent = false)
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
        var epoch = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var head = new MailboxIntelligenceHeadDto(
            mailboxId,
            WinoIntelligenceVersions.V1,
            epoch,
            coveredIds.Count == 0 ? 0 : 1,
            coveredIds.Count,
            coveredIds.Count * 3_072L,
            coveredIds.Count == 0 ? null : now,
            coveredIds.Count == 0 ? null : now,
            now,
            now);
        apiClient.Setup(client => client.GetSemanticMailboxesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new SemanticMailboxDto(
                mailboxId,
                account.Address,
                (int)account.ProviderType,
                null)]);
        if (headInitiallyAbsent)
        {
            apiClient.SetupSequence(client => client.GetIntelligenceHeadAsync(
                    mailboxId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync((MailboxIntelligenceHeadDto?)null)
                .ReturnsAsync(head);
        }
        else
        {
            apiClient.Setup(client => client.GetIntelligenceHeadAsync(
                    mailboxId,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(head);
        }
        apiClient.Setup(client => client.BeginIntelligenceReindexAsync(
                mailboxId,
                It.IsAny<BeginIntelligenceReindexRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BeginIntelligenceReindexResultDto(
                mailboxId,
                WinoIntelligenceVersions.V1,
                epoch,
                false));
        apiClient.Setup(client => client.ReconcileMessageIntelligenceAsync(
                mailboxId,
                It.IsAny<ReconcileMessageIntelligenceRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, ReconcileMessageIntelligenceRequest request, CancellationToken _) =>
                new ReconcileMessageIntelligenceResultDto(
                    WinoIntelligenceVersions.V1,
                    epoch,
                    request.ServerMessageKeys.Where(coveredIds.Contains).ToArray(),
                    request.ServerMessageKeys.Where(id => !coveredIds.Contains(id)).ToArray()));
        apiClient.Setup(client => client.GetIntelligenceChangesAsync(
                mailboxId,
                WinoIntelligenceVersions.V1,
                epoch,
                It.IsAny<long>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IntelligenceChangesPageDto(
                WinoIntelligenceVersions.V1,
                epoch,
                head.ArtifactRevision,
                head.ArtifactRevision,
                coveredIds.Select((id, index) => new IntelligenceDocumentChangeDto(
                    index + 1,
                    id,
                    false,
                    Document(id, index + 1),
                    now)).ToArray()));
        var localStore = new Mock<ILocalIntelligenceStore>();
        localStore.Setup(store => store.GetMailboxStateAsync(
                account.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalIntelligenceMailboxState(
                account.Id,
                mailboxId,
                WinoIntelligenceVersions.V1,
                epoch,
                0));
        localStore.Setup(store => store.ApplyChangesAsync(
                account.Id,
                mailboxId,
                It.IsAny<IntelligenceChangesPageDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalIntelligenceChangeApplyResult(
                coveredIds.ToHashSet(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                head.ArtifactRevision));
        localStore.Setup(store => store.GetCurrentDocumentsAsync(
                account.Id,
                It.IsAny<IReadOnlyCollection<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, IReadOnlyCollection<string> keys, CancellationToken _) =>
                keys.Where(coveredIds.Contains).ToDictionary(
                    static id => id,
                    static id => Document(id, 1),
                    StringComparer.Ordinal));
        var messageResolver = new Mock<IIntelligenceMessageContextResolver>();
        var database = new InMemoryDatabaseService();
        await database.InitializeAsync();
        var winoUserId = Guid.NewGuid();
        await database.Connection.InsertAsync(new WinoAccount
        {
            Id = winoUserId,
        });
        using var rsa = RSA.Create(2048);
        const string keyId = "test-intelligence-key";
        var encryptor = new PemContentEnvelopeEncryptor(new ContentEncryptionPublicKey(
            keyId,
            rsa.ExportSubjectPublicKeyInfoPem()));
        var decryptor = new PemContentEnvelopeDecryptor(new Dictionary<string, string>
        {
            [keyId] = rsa.ExportPkcs8PrivateKeyPem(),
        });
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
            messenger);

        return new Fixture(
            coordinator,
            account,
            mailboxId,
            apiClient,
            localStore,
            messageResolver,
            database,
            messenger,
            registry,
            epoch,
            winoUserId,
            decryptor);
    }

    private static async Task WaitForCompletionAsync(
        ISemanticIndexCoordinator coordinator,
        Guid accountId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (coordinator.GetJobSnapshot(accountId).IsActive)
            await Task.Delay(10, timeout.Token);
    }

    private static MessageIntelligenceDownloadDto Document(string serverMessageKey, long revision)
        => new()
        {
            ServerMessageKey = serverMessageKey,
            ContentHash = $"hash-{serverMessageKey}",
            Subject = serverMessageKey,
            Sender = "sender@example.test",
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            IsOutgoing = false,
            IsRead = false,
            IsFlagged = false,
            HasAttachments = false,
            FolderIds = ["inbox"],
            SenderAddresses = ["sender@example.test"],
            RecipientAddresses = ["mail@example.test"],
            Analysis = new MessageIntelligenceDocumentV1
            {
                SourceLanguage = "en",
                Headline = serverMessageKey,
                Summary = serverMessageKey,
                Category = MessageCategoryV1.Conversation,
                Intent = MessageIntentV1.Inform,
                Urgency = MessageUrgencyV1.Normal,
                Confidence = 1,
            },
            Embedding = Convert.ToBase64String(new byte[3_072]),
            EmbeddingDimensions = 768,
            EmbeddingEncoding = "float32-le",
            ArtifactRevision = revision,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
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

    private static SemanticMailContent Content(string recipient)
        => new(
            new MailBodyContent(MailBodyFormat.PlainText, "Message body"),
            [new MailAddress("sender@example.test", "Sender")],
            [recipient],
            []);

    private sealed record Fixture(
        SemanticIndexCoordinator Coordinator,
        MailAccount Account,
        Guid MailboxId,
        Mock<IWinoAccountApiClient> ApiClient,
        Mock<ILocalIntelligenceStore> LocalStore,
        Mock<IIntelligenceMessageContextResolver> MessageResolver,
        InMemoryDatabaseService Database,
        StrongReferenceMessenger Messenger,
        SemanticIndexJobRegistry Registry,
        Guid Epoch,
        Guid WinoUserId,
        PemContentEnvelopeDecryptor Decryptor) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Database.DisposeAsync();
    }
}
