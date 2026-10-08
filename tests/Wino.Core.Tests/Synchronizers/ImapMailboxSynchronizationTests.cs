using FluentAssertions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Synchronization;
using Wino.Core.Extensions;
using Wino.Services.Extensions;
using Wino.Core.Synchronizers.Errors.Imap;
using Wino.Core.Domain.Enums;
using Wino.Core.Synchronizers.ImapSync;
using Xunit;
using IMailService = Wino.Core.Domain.Interfaces.IMailService;

namespace Wino.Core.Tests.Synchronizers;

public sealed class ImapMailboxSynchronizationTests
{
    [Fact]
    public async Task LegacyFlags_UsesOneCompactFetchFor5000Messages_AndDetectsDeletions()
    {
        var f = new Fixture(5000);
        f.RemoteFlags.RemoveAt(0); // Expunged on another client.
        f.RemoteFlags[0] = Summary(2, MessageFlags.Seen | MessageFlags.Flagged);
        f.RemoteFlags[1] = Summary(3, MessageFlags.Deleted);

        (await f.Sync()).Success.Should().BeTrue();

        f.Fetches.Should().ContainSingle();
        f.Fetches[0].Range.Should().Be("1:5000");
        f.Fetches[0].ChangedSince.Should().BeNull();
        f.Updates.Should().ContainSingle(x => x.MailCopyId == f.Id(2) && x.IsRead == true && x.IsFlagged == true);
        f.Deleted.Should().BeEquivalentTo([f.Id(1), f.Id(3)]);
        f.Mail.Verify(x => x.GetMailsByFolderIdAsync(It.IsAny<Guid>()), Times.Never);
        f.Searches.Should().ContainSingle(); // First-use hole repair, no UNSEEN/FLAGGED/existence searches.
        f.Fetches.Clear();
        f.Searches.Clear();
        (await f.Sync()).Success.Should().BeTrue();
        f.Searches.Should().BeEmpty();
        f.Fetches.Should().ContainSingle();
    }

    [Fact]
    public async Task Condstore_UsesChangedSince_AndChecksMembershipOnEveryDelta()
    {
        var f = new Fixture(3, condstore: true);
        f.RemoteFlags = [Summary(2, MessageFlags.Seen)];
        f.SearchResult = [new UniqueId(1), new UniqueId(2)];
        (await f.Sync()).Success.Should().BeTrue();
        f.Fetches.Should().ContainSingle().Which.ChangedSince.Should().Be(20);
        f.Deleted.Should().Equal(f.Id(3)); // Missing from delta alone does not delete UID 1.
        var repaired = f.Folder.LastUidReconcileUtc;
        f.Searches.Clear();
        f.Deleted.Clear();
        (await f.Sync()).Success.Should().BeTrue();
        f.Searches.Should().ContainSingle(); // Membership only; must not reset the hole-repair timer.
        f.Folder.LastUidReconcileUtc.Should().Be(repaired);
    }

    [Fact]
    public async Task MailboxWithoutModSequences_FallsBackDespiteServerCapability()
    {
        var f = new Fixture(2, condstore: true);
        f.Remote.Setup(x => x.Supports(FolderFeature.ModSequences)).Returns(false);
        (await f.Sync()).Success.Should().BeTrue();
        f.Fetches.Should().ContainSingle().Which.ChangedSince.Should().BeNull();
        f.Folder.HighestModeSeq.Should().Be(0);
    }

    [Fact]
    public async Task RegressedModSequence_ForcesFullFlagsBeforeSavingANewBaseline()
    {
        var f = new Fixture(2, condstore: true);
        f.Remote.SetupGet(x => x.HighestModSeq).Returns(10);
        (await f.Sync()).Success.Should().BeTrue();
        f.Fetches.Should().ContainSingle().Which.ChangedSince.Should().BeNull();
        f.Folder.HighestModeSeq.Should().Be(0);
    }

    [Fact]
    public async Task RejectedChangedSince_FallsBackAndRemembersMailboxRestriction()
    {
        var f = new Fixture(2, condstore: true) { RejectChangedSince = true };
        (await f.Sync()).Success.Should().BeTrue();
        f.Fetches.Select(x => x.ChangedSince).Should().Equal(20UL, null);
        f.Fetches.Clear();
        (await f.Sync()).Success.Should().BeTrue();
        f.Fetches.Should().ContainSingle().Which.ChangedSince.Should().BeNull();
    }

    [Fact]
    public async Task FailedFlagFetch_DoesNotDeleteMessagesOrAdvanceCheckpoint()
    {
        var f = new Fixture(3) { FetchFailure = new IOException("connection reset mid-response") };
        var before = f.Folder.LastUidReconcileUtc;
        f.Remote.SetupGet(x => x.UidNext).Returns(new UniqueId(100));
        (await f.Sync()).Success.Should().BeFalse();
        f.Folder.HighestKnownUid.Should().Be(3);
        f.Folder.HighestModeSeq.Should().Be(20);
        f.Folder.LastUidReconcileUtc.Should().Be(before);
        f.Deleted.Should().BeEmpty();
        f.Folders.Verify(x => x.UpdateFolderAsync(It.IsAny<MailItemFolder>()), Times.Never);
    }

    [Fact]
    public async Task FailedCheckpointWrite_RestoresInMemoryState_AndRetriesHoleRepair()
    {
        var f = new Fixture(2);
        f.Folders.Setup(x => x.UpdateFolderAsync(f.Folder)).ThrowsAsync(new IOException("database busy"));
        (await f.Sync()).Success.Should().BeFalse();
        f.Folder.HighestModeSeq.Should().Be(20);
        f.Folder.LastUidReconcileUtc.Should().Be(f.OriginalRepairTime);
        f.Folders.Setup(x => x.UpdateFolderAsync(f.Folder)).Returns(Task.CompletedTask);
        f.Searches.Clear();
        (await f.Sync()).Success.Should().BeTrue();
        f.Searches.Should().ContainSingle();
    }

    [Fact]
    public async Task LaterServerFrontier_IsNotCommittedBeforeItIsProcessed()
    {
        var f = new Fixture(2, condstore: true);
        f.AfterFetch = () =>
        {
            f.Remote.SetupGet(x => x.UidNext).Returns(new UniqueId(99));
            f.Remote.SetupGet(x => x.HighestModSeq).Returns(1000);
        };
        (await f.Sync()).Success.Should().BeTrue();
        f.Folder.HighestKnownUid.Should().Be(2);
        f.Folder.HighestModeSeq.Should().Be(30);
    }

    [Fact]
    public async Task MissingMetadataThatStillExists_DoesNotAdvanceCheckpoint()
    {
        var f = new Fixture(0);
        f.Remote.SetupGet(x => x.UidNext).Returns(new UniqueId(11));
        f.SearchResult = [new UniqueId(10)];
        f.RemoteFlags = [];
        (await f.Sync()).Success.Should().BeFalse();
        f.Folder.HighestKnownUid.Should().Be(0);
        f.Deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task OnlineSearchDownload_DoesNotMoveFolderHighWaterMark()
    {
        var f = new Fixture(1);
        f.RemoteFlags = [Summary(90, MessageFlags.None)];
        f.Remote.SetupGet(x => x.UidNext).Returns(new UniqueId(101));
        f.Owner.Setup(x => x.CreateNewMailPackagesAsync(It.IsAny<ImapMessageCreationPackage>(), f.Folder, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new NewMailItemPackage(new MailCopy { Id = f.Id(90) }, null, "INBOX")]);
        f.Mail.Setup(x => x.CreateMailAsync(It.IsAny<Guid>(), It.IsAny<NewMailItemPackage>())).ReturnsAsync(true);
        var downloaded = await f.Sut.DownloadMessagesByUidsAsync(f.Client.Object, f.Remote.Object, f.Folder,
            [new UniqueId(90)], f.Owner.Object);
        downloaded.Should().Equal(f.Id(90));
        f.Folder.HighestKnownUid.Should().Be(1);
    }

    [Fact]
    public async Task FailedMetadataPersistence_DoesNotAdvanceCheckpoint()
    {
        var f = new Fixture(0);
        f.SearchResult = [new UniqueId(10)];
        f.RemoteFlags = [Summary(10, MessageFlags.None)];
        f.Owner.Setup(x => x.CreateNewMailPackagesAsync(It.IsAny<ImapMessageCreationPackage>(), f.Folder, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new NewMailItemPackage(new MailCopy { Id = f.Id(10) }, null, "INBOX")]);
        (await f.Sync()).Success.Should().BeFalse();
        f.Folder.HighestKnownUid.Should().Be(0);
    }

    [Fact]
    public async Task ChangedUidValidity_PreservesFolderSettingsAndLocalDrafts()
    {
        var f = new Fixture(1);
        var draft = new MailCopy { Id = "local", IsDraft = true, DraftId = "localDraft_123" };
        f.Folder.IsSticky = true;
        f.Folder.IsSynchronizationEnabled = true;
        f.Remote.SetupGet(x => x.UidValidity).Returns(99);
        f.Mail.Setup(x => x.GetMailsByFolderIdAsync(f.Folder.Id)).ReturnsAsync([.. f.Known, draft]);
        f.Mail.SetupSequence(x => x.GetImapSynchronizationMailsAsync(f.Folder.Id))
            .ReturnsAsync(f.Known).ReturnsAsync([draft]);
        f.SearchResult = [];
        (await f.Sync()).Success.Should().BeTrue();
        f.Deleted.Should().Equal(f.Id(1));
        f.Folder.UidValidity.Should().Be(99);
        f.Folder.IsSticky.Should().BeTrue();
        f.Folder.IsSynchronizationEnabled.Should().BeTrue();
        f.Folders.Verify(x => x.DeleteFolderAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task InitialSync_Uses200MessageBatchesAndRequiredHeaders()
    {
        var f = new Fixture(0);
        f.Remote.SetupGet(x => x.UidNext).Returns(new UniqueId(402));
        f.SearchResult = Enumerable.Range(1, 401).Select(uid => new UniqueId((uint)uid)).ToList();
        f.RemoteFlags = f.SearchResult.Select(uid => Summary(uid.Id, MessageFlags.None)).ToList();
        f.Owner.Setup(x => x.CreateNewMailPackagesAsync(It.IsAny<ImapMessageCreationPackage>(), f.Folder, It.IsAny<CancellationToken>()))
            .Returns<ImapMessageCreationPackage, MailItemFolder, CancellationToken>((package, _, _) =>
                Task.FromResult(new List<NewMailItemPackage> { new(new MailCopy { Id = f.Id(package.MessageSummary.UniqueId.Id) }, null, "INBOX") }));
        f.Mail.Setup(x => x.CreateMailAsync(It.IsAny<Guid>(), It.IsAny<NewMailItemPackage>())).ReturnsAsync(true);
        (await f.Sync()).Success.Should().BeTrue();
        f.Fetches.Select(fetch => fetch.Range).Should().Equal("1:200", "201:400", "401");
        f.Requests.Should().OnlyContain(request => !request.Items.HasFlag(MessageSummaryItems.Headers));
        f.Requests.Should().OnlyContain(request => request.Headers.Contains("X-Wino-Draft-Id") && request.Headers.Contains("Disposition-Notification-To"));
        f.Folder.HighestKnownUid.Should().Be(401);
        f.Mail.Verify(x => x.CreateMailAsync(It.IsAny<Guid>(), It.IsAny<NewMailItemPackage>()), Times.Exactly(401));
    }

    [Fact]
    public async Task CancellationDuringMetadataWrites_PreservesCheckpoint()
    {
        var f = new Fixture(0);
        using var cancellation = new CancellationTokenSource();
        f.SearchResult = [new UniqueId(1), new UniqueId(2)];
        f.RemoteFlags = [Summary(1, MessageFlags.None), Summary(2, MessageFlags.None)];
        f.Owner.Setup(x => x.CreateNewMailPackagesAsync(It.IsAny<ImapMessageCreationPackage>(), f.Folder, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new NewMailItemPackage(new MailCopy { Id = f.Id(1) }, null, "INBOX")]);
        f.Mail.Setup(x => x.CreateMailAsync(It.IsAny<Guid>(), It.IsAny<NewMailItemPackage>()))
            .Callback(() => cancellation.Cancel()).ReturnsAsync(true);
        var sync = () => f.Sut.SynchronizeFolderAsync(f.Client.Object, f.Folder, f.Owner.Object, "imap.example.com", cancellation.Token);
        await sync.Should().ThrowAsync<OperationCanceledException>();
        f.Folder.HighestKnownUid.Should().Be(0);
        f.Folder.LastUidReconcileUtc.Should().Be(f.OriginalRepairTime);
        f.Folders.Verify(x => x.UpdateFolderAsync(It.IsAny<MailItemFolder>()), Times.Never);
    }

    [Fact]
    public async Task UnavailableMailbox_IsRecoverableAndPreservesItsCheckpoint()
    {
        var f = new Fixture(2);
        f.Client.Setup(x => x.GetFolderAsync("INBOX", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FolderNotFoundException("INBOX"));
        var handler = new ImapFolderNotFoundHandler();
        var errors = new Mock<IImapSynchronizerErrorHandlerFactory>();
        errors.Setup(x => x.HandleErrorAsync(It.IsAny<SynchronizerErrorContext>()))
            .Returns<SynchronizerErrorContext>(handler.HandleAsync);
        var sut = new UnifiedImapSynchronizer(f.Folders.Object, f.Mail.Object, errors.Object);
        (await sut.SynchronizeFolderAsync(f.Client.Object, f.Folder, f.Owner.Object, "imap.example.com")).Success.Should().BeFalse();
        f.Folder.HighestKnownUid.Should().Be(2);
        f.Deleted.Should().BeEmpty();
        f.Folders.Verify(x => x.DeleteFolderAsync(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task IncompleteNewMessageMetadata_DoesNotCreateBlankMailOrAdvanceCheckpoint()
    {
        var f = new Fixture(0);
        f.SearchResult = [new UniqueId(10)];
        f.RemoteFlags = [new MessageSummary(0) { UniqueId = new UniqueId(10), Flags = MessageFlags.Seen }];
        (await f.Sync()).Success.Should().BeFalse();
        f.Folder.HighestKnownUid.Should().Be(0);
        f.Mail.Verify(x => x.CreateMailAsync(It.IsAny<Guid>(), It.IsAny<NewMailItemPackage>()), Times.Never);
    }

    private static IMessageSummary Summary(uint uid, MessageFlags flags)
        => new MessageSummary(0) { UniqueId = new UniqueId(uid), Flags = flags, Envelope = new Envelope(), InternalDate = DateTimeOffset.UtcNow };

    private sealed class Fixture
    {
        public readonly Mock<IImapClient> Client = new();
        public readonly Mock<IMailFolder> Remote = new();
        public readonly Mock<IMailService> Mail = new();
        public readonly Mock<IFolderService> Folders = new();
        public readonly Mock<IImapSynchronizer> Owner = new();
        public readonly MailItemFolder Folder = new()
        {
            Id = Guid.NewGuid(), MailAccountId = Guid.NewGuid(), RemoteFolderId = "INBOX",
            FolderName = "Inbox", UidValidity = 7, HighestModeSeq = 20, LastUidReconcileUtc = DateTime.UtcNow
        };
        public readonly UnifiedImapSynchronizer Sut;
        public DateTime? OriginalRepairTime;
        public List<MailCopy> Known;
        public List<IMessageSummary> RemoteFlags;
        public IList<UniqueId> SearchResult;
        public List<SearchQuery> Searches = [];
        public List<(string Range, ulong? ChangedSince)> Fetches = [];
        public List<MailCopyStateUpdate> Updates = [];
        public List<IFetchRequest> Requests = [];
        public List<string> Deleted = [];
        public bool RejectChangedSince;
        public Exception? FetchFailure;
        public Action? AfterFetch;
        public string Id(uint uid) => MailkitClientExtensions.CreateUid(Folder.Id, uid);
        public Task<FolderSyncResult> Sync() => Sut.SynchronizeFolderAsync(Client.Object, Folder, Owner.Object, "imap.example.com");

        public Fixture(int count, bool condstore = false)
        {
            OriginalRepairTime = Folder.LastUidReconcileUtc;
            Folder.HighestKnownUid = (uint)count;
            Known = Enumerable.Range(1, count).Select(uid => new MailCopy
            {
                Id = Id((uint)uid), UniqueId = Guid.NewGuid(), FolderId = Folder.Id,
                ImapUid = (uint)uid, ImapUidValidity = 7
            }).ToList();
            RemoteFlags = Known.Select(mail => Summary(mail.ImapUid, MessageFlags.None)).ToList();
            SearchResult = Known.Select(mail => new UniqueId(mail.ImapUid)).ToList();
            Client.SetupGet(x => x.IsConnected).Returns(true);
            Client.SetupGet(x => x.Capabilities).Returns(condstore ? ImapCapabilities.CondStore : ImapCapabilities.None);
            Client.Setup(x => x.GetFolderAsync("INBOX", It.IsAny<CancellationToken>())).ReturnsAsync(Remote.Object);
            Remote.SetupGet(x => x.IsOpen).Returns(true);
            Remote.SetupGet(x => x.UidValidity).Returns(7);
            Remote.SetupGet(x => x.UidNext).Returns(new UniqueId((uint)count + 1));
            Remote.SetupGet(x => x.HighestModSeq).Returns(30);
            Remote.Setup(x => x.Supports(FolderFeature.ModSequences)).Returns(condstore);
            Remote.Setup(x => x.SearchAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>()))
                .Callback<SearchQuery, CancellationToken>((query, _) => Searches.Add(query))
                .ReturnsAsync(() => SearchResult);
            Remote.Setup(x => x.FetchAsync(It.IsAny<IList<UniqueId>>(), It.IsAny<IFetchRequest>(), It.IsAny<CancellationToken>()))
                .Returns<IList<UniqueId>, IFetchRequest, CancellationToken>((uids, request, _) =>
                {
                    Fetches.Add((uids.ToString()!, request.ChangedSince));
                    Requests.Add(request);
                    if (FetchFailure != null) throw FetchFailure;
                    if (RejectChangedSince && request.ChangedSince.HasValue)
                        throw new ImapCommandException(ImapCommandResponse.Bad, "unsupported CHANGEDSINCE");
                    AfterFetch?.Invoke();
                    return Task.FromResult<IList<IMessageSummary>>(RemoteFlags.Where(summary => uids.Contains(summary.UniqueId)).ToList());
                });
            Mail.Setup(x => x.GetImapSynchronizationMailsAsync(Folder.Id)).ReturnsAsync(() => Known);
            Mail.Setup(x => x.GetExistingMailsAsync(Folder.Id, It.IsAny<IEnumerable<UniqueId>>()))
                .Returns<Guid, IEnumerable<UniqueId>>((_, uids) => Task.FromResult(Known.Where(mail => uids.Contains(new UniqueId(mail.ImapUid))).ToList()));
            Mail.Setup(x => x.ApplyMailStateUpdatesAsync(It.IsAny<IEnumerable<MailCopyStateUpdate>>()))
                .Callback<IEnumerable<MailCopyStateUpdate>>(updates => Updates.AddRange(updates)).Returns(Task.CompletedTask);
            Mail.Setup(x => x.DeleteMailsAsync(Folder.MailAccountId, It.IsAny<IEnumerable<string>>()))
                .Callback<Guid, IEnumerable<string>>((_, ids) => Deleted.AddRange(ids)).Returns(Task.CompletedTask);
            Folders.Setup(x => x.GetKnownUidsForFolderAsync(Folder.Id)).ReturnsAsync(() => Known.Select(mail => mail.ImapUid).ToList());
            var errors = new Mock<IImapSynchronizerErrorHandlerFactory>();
            errors.Setup(x => x.HandleErrorAsync(It.IsAny<SynchronizerErrorContext>()))
                .Callback<SynchronizerErrorContext>(error => error.Severity = SynchronizerErrorSeverity.Transient).ReturnsAsync(true);
            Sut = new UnifiedImapSynchronizer(Folders.Object, Mail.Object, errors.Object);
        }
    }
}
