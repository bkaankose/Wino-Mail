using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Intelligence;
using Wino.Core.Domain.Intelligence.Keys;
using Wino.Core.Domain.Models.SemanticIndexing;
using Wino.Mail.AI.Abstractions;
using Wino.Mail.Api.Contracts.Common;
using Wino.Mail.Contracts.Intelligence;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.Server;
using Wino.Messaging.UI;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Intelligence;

/// <summary>
/// How the coordinator turns synchronized mail into automatic intelligence jobs.
/// Submissions stop at the registry stub: what matters here is which messages reach it and when.
/// </summary>
public sealed class MailIntelligenceCoordinatorAutomaticTests : IAsyncDisposable
{
    private static readonly Guid AccountId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid WinoUserId = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");

    private readonly StrongReferenceMessenger _messenger = new();
    private readonly Mock<IAccountService> _accounts = new();
    private readonly Mock<ILocalIntelligenceService> _local = new();
    private readonly Mock<IWinoAccountIntelligenceSnapshotService> _entitlement = new();
    private readonly RecordingRegistry _registry = new();
    private readonly MailAccount _account;
    private readonly MailIntelligenceCoordinator _coordinator;

    public MailIntelligenceCoordinatorAutomaticTests()
    {
        _account = new MailAccount
        {
            Id = AccountId,
            ProviderType = MailProviderType.Outlook,
            Preferences = new MailAccountPreferences
            {
                IsSemanticIndexingEnabled = true,
                AutomaticallyIndexNewMessages = true,
                IsIntelligenceFolderSelectionInitialized = true,
                SelectedIntelligenceFolderIds = new HashSet<string>(StringComparer.Ordinal) { "inbox" },
            },
        };
        _accounts.Setup(x => x.GetAccountAsync(AccountId)).ReturnsAsync(_account);
        _local.Setup(x => x.ShouldAutomaticallyProcessAsync(AccountId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        SetEntitlement(WinoIntelligenceEntitlementState.Active);

        var apiClient = Mock.Of<IWinoAccountApiClient>();
        var keys = Mock.Of<IIntelligenceResultKeyStore>();
        _coordinator = new MailIntelligenceCoordinator(
            Mock.Of<IDatabaseService>(),
            _accounts.Object,
            apiClient,
            Mock.Of<IMailIntelligenceStore>(),
            _local.Object,
            new MailIntelligenceUploadBuilder(),
            keys,
            new IntelligenceTransportKeyProvider(apiClient),
            new MailIntelligenceResultPageReader(apiClient, keys),
            _registry,
            Mock.Of<ITranslationService>(),
            Mock.Of<IIntelligenceMessageContextResolver>(),
            _messenger,
            _entitlement.Object);
    }

    public async ValueTask DisposeAsync() => await _coordinator.DisposeAsync();

    [Fact]
    public async Task NewInboxMail_IsSubmittedWhenSynchronizationCompletes()
    {
        await _coordinator.InitializeAsync();

        _messenger.Send(new MailAddedMessage(Mail("m1", "inbox", SpecialFolderType.Inbox)));
        _messenger.Send(new BulkMailAddedMessage([Mail("m2", "inbox", SpecialFolderType.Inbox), Mail("m3", "archive", SpecialFolderType.Archive)]));
        _messenger.Send(SyncCompleted());

        var ids = await _registry.NextStartAsync();
        ids.Should().BeEquivalentTo([Remote("m1"), Remote("m2")]);
    }

    [Fact]
    public async Task MailFromTheClient_IsNotCaptured()
    {
        await _coordinator.InitializeAsync();

        _messenger.Send(new MailAddedMessage(Mail("reverted", "inbox", SpecialFolderType.Inbox), EntityUpdateSource.ClientReverted));
        _messenger.Send(SyncCompleted());

        await _registry.ExpectNoStartAsync();
    }

    [Fact]
    public async Task WhileASubmissionRuns_NewMailIsSubmittedAfterItEnds()
    {
        await _coordinator.InitializeAsync();
        var running = _registry.HoldBusy();

        _messenger.Send(new MailAddedMessage(Mail("m1", "inbox", SpecialFolderType.Inbox)));
        _messenger.Send(SyncCompleted());
        await _registry.BusyAttemptAsync();

        _messenger.Send(new MailAddedMessage(Mail("m2", "inbox", SpecialFolderType.Inbox)));
        _messenger.Send(SyncCompleted());
        await _registry.BusyAttemptAsync();

        _registry.ReleaseBusy(running);

        var ids = await _registry.NextStartAsync();
        ids.Should().BeEquivalentTo([Remote("m1"), Remote("m2")]);
    }

    [Fact]
    public async Task SettingOff_DiscardsTheCapturedMail()
    {
        _local.Setup(x => x.ShouldAutomaticallyProcessAsync(AccountId, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await _coordinator.InitializeAsync();

        _messenger.Send(new MailAddedMessage(Mail("m1", "inbox", SpecialFolderType.Inbox)));
        _messenger.Send(SyncCompleted());
        await _registry.ExpectNoStartAsync();

        _local.Setup(x => x.ShouldAutomaticallyProcessAsync(AccountId, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _messenger.Send(SyncCompleted());
        await _registry.ExpectNoStartAsync();
    }

    [Fact]
    public async Task WithoutQuota_MailWaitsUntilTheEntitlementAllowsWork()
    {
        SetEntitlement(WinoIntelligenceEntitlementState.QuotaExhausted);
        await _coordinator.InitializeAsync();

        _messenger.Send(new MailAddedMessage(Mail("m1", "inbox", SpecialFolderType.Inbox)));
        _messenger.Send(SyncCompleted());
        await _registry.ExpectNoStartAsync();

        var active = SetEntitlement(WinoIntelligenceEntitlementState.Active);
        _messenger.Send(new WinoIntelligenceEntitlementChanged(active));

        var ids = await _registry.NextStartAsync();
        ids.Should().Equal(Remote("m1"));
    }

    [Fact]
    public async Task WithoutCurrentConsent_MailIsHeldBack()
    {
        var snapshot = WinoAccountIntelligenceSnapshot.Empty(WinoUserId) with
        {
            Consent = new IntelligenceConsentDto(ConsentStatuses.NotAccepted, "intelligence-v1", null, null, null,
                "https://www.winomail.app/privacy", IntelligenceDeletionStatuses.NotRequired),
        };
        _entitlement.Setup(x => x.GetCachedAsync(WinoUserId, It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        await _coordinator.InitializeAsync();

        _messenger.Send(new MailAddedMessage(Mail("m1", "inbox", SpecialFolderType.Inbox)));
        _messenger.Send(SyncCompleted());

        await _registry.ExpectNoStartAsync();
    }

    private WinoIntelligenceEntitlementSnapshot SetEntitlement(WinoIntelligenceEntitlementState state)
    {
        var snapshot = new WinoIntelligenceEntitlementSnapshot(state, WinoUserId, DateTimeOffset.UtcNow);
        _entitlement.SetupGet(x => x.CurrentEntitlement).Returns(snapshot);
        return snapshot;
    }

    /// <summary>The intelligence identity the coordinator derives for an Outlook message id.</summary>
    private static string Remote(string providerMessageId) => RemoteMessageId.ForOutlook(providerMessageId);

    private static AccountSynchronizationCompleted SyncCompleted()
        => new(AccountId, SynchronizationCompletedState.Success, null, MailSynchronizationType.InboxOnly);

    private MailCopy Mail(string id, string remoteFolderId, SpecialFolderType folderType) => new()
    {
        Id = id,
        AssignedAccount = _account,
        AssignedFolder = new MailItemFolder { RemoteFolderId = remoteFolderId, SpecialFolderType = folderType },
    };

    /// <summary>
    /// Stands in for the job registry. It never runs a worker; it records what each start would
    /// have submitted by running the worker far enough to publish its first snapshot.
    /// </summary>
    private sealed class RecordingRegistry : ISemanticIndexJobRegistry
    {
        private readonly BlockingCollection<IReadOnlyList<string>> _starts = [];
        private readonly BlockingCollection<bool> _busyAttempts = [];
        private TaskCompletionSource? _busy;
        private readonly Lock _gate = new();

        public bool TryStart(Guid accountId, Func<CancellationToken, Task> worker, out Task task)
        {
            lock (_gate)
            {
                if (_busy is { } busy)
                {
                    task = busy.Task;
                    _busyAttempts.Add(true);
                    return false;
                }
            }

            task = Task.CompletedTask;
            _starts.Add(IdsOf(worker));
            return true;
        }

        public bool IsRunning(Guid accountId) => _busy is not null;
        public Task CancelAndWaitAsync(Guid accountId) => Task.CompletedTask;
        public Task CancelAllAndWaitAsync() => Task.CompletedTask;

        public TaskCompletionSource HoldBusy()
        {
            lock (_gate)
            {
                return _busy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void ReleaseBusy(TaskCompletionSource busy)
        {
            lock (_gate)
            {
                _busy = null;
            }

            busy.SetResult();
        }

        public Task<IReadOnlyList<string>> NextStartAsync()
            => Task.Run(() => _starts.Take(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token));

        public Task BusyAttemptAsync()
            => Task.Run(() => _busyAttempts.Take(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token));

        public async Task ExpectNoStartAsync()
        {
            await Task.Delay(300);
            _starts.Count.Should().Be(0);
        }

        /// <summary>
        /// The ids live in the worker's closure. Running the worker would only publish their
        /// count, so the closure object is read instead: it holds exactly one list of strings.
        /// </summary>
        private static IReadOnlyList<string> IdsOf(Func<CancellationToken, Task> worker)
        {
            var target = worker.Target ?? throw new InvalidOperationException("The worker has no closure.");
            var field = target.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .Select(f => f.GetValue(target))
                .OfType<IReadOnlyList<string>>()
                .FirstOrDefault();
            return field ?? throw new InvalidOperationException("The worker closure holds no message ids.");
        }
    }
}
