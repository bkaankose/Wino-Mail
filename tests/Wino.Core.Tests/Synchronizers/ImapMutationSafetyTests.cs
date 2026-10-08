using FluentAssertions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Integration.Processors;
using Wino.Core.Requests.Bundles;
using Wino.Core.Requests.Mail;
using Wino.Core.Requests.Folder;
using Wino.Core.Synchronizers.ImapSync;
using Wino.Core.Synchronizers.Mail;
using Xunit;
using IMailService = Wino.Core.Domain.Interfaces.IMailService;

namespace Wino.Core.Tests.Synchronizers;

public sealed class ImapMutationSafetyTests
{
    [Theory]
    [InlineData("read")]
    [InlineData("flag")]
    [InlineData("delete")]
    [InlineData("move")]
    public async Task ChangedMailboxGeneration_BlocksEveryUidMutation(string operation)
    {
        var sut = CreateSynchronizer();
        try
        {
            var folder = new MailItemFolder { Id = Guid.NewGuid(), RemoteFolderId = "INBOX", UidValidity = 7 };
            var copy = new MailCopy { Id = "legacy", ImapUid = 5, ImapUidValidity = 7, AssignedFolder = folder, FolderId = folder.Id };
            var remote = new Mock<IMailFolder>();
            remote.SetupGet(x => x.UidValidity).Returns(8);
            var client = new Mock<IImapClient>();
            client.Setup(x => x.GetFolderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(remote.Object);
            var bundle = operation switch
            {
                "read" => sut.MarkRead(new BatchMarkReadRequest(new[] { new MarkReadRequest(copy, true) })),
                "flag" => sut.ChangeFlag(new BatchChangeFlagRequest(new[] { new ChangeFlagRequest(copy, true) })),
                "delete" => sut.Delete(new BatchDeleteRequest(new[] { new DeleteRequest(copy) })),
                _ => sut.Move(new BatchMoveRequest(new[] { new MoveRequest(copy, folder, new MailItemFolder { RemoteFolderId = "Archive" }) }))
            };
            var execute = () => bundle[0].NativeRequest.ExecuteAsync(client.Object, bundle[0].Request, CancellationToken.None);
            await execute.Should().ThrowAsync<InvalidOperationException>();
            remote.Verify(x => x.StoreAsync(It.IsAny<IList<UniqueId>>(), It.IsAny<IStoreFlagsRequest>(), It.IsAny<CancellationToken>()), Times.Never);
            remote.Verify(x => x.MoveToAsync(It.IsAny<IList<UniqueId>>(), It.IsAny<IMailFolder>(), It.IsAny<CancellationToken>()), Times.Never);
            remote.Verify(x => x.CopyToAsync(It.IsAny<IList<UniqueId>>(), It.IsAny<IMailFolder>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally { await sut.KillSynchronizerAsync(); }
    }

    [Fact]
    public void LegacyCopy_UsesSavedFolderGeneration_AndUnknownGenerationIsRejected()
    {
        var remote = new Mock<IMailFolder>();
        remote.SetupGet(x => x.UidValidity).Returns(7);
        var copy = new MailCopy { ImapUidValidity = 0, AssignedFolder = new MailItemFolder { UidValidity = 7 } };
        ImapSynchronizer.EnsureMessageUidValidity(remote.Object, new[] { copy });
        copy.AssignedFolder.UidValidity = 0;
        var validate = () => ImapSynchronizer.EnsureMessageUidValidity(remote.Object, new[] { copy });
        validate.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyDeleteOrMove_NeverRunsMailboxWideExpunge(bool move)
    {
        var sut = CreateSynchronizer();
        try
        {
            var folder = new MailItemFolder { Id = Guid.NewGuid(), RemoteFolderId = "INBOX", UidValidity = 7 };
            var copy = new MailCopy { ImapUid = 5, ImapUidValidity = 7, AssignedFolder = folder, FolderId = folder.Id };
            var remote = new Mock<IMailFolder>();
            remote.SetupGet(x => x.UidValidity).Returns(7);
            remote.Setup(x => x.SearchAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
            var client = new Mock<IImapClient>();
            client.Setup(x => x.GetFolderAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(remote.Object);
            var bundles = move
                ? sut.Move(new BatchMoveRequest(new[] { new MoveRequest(copy, folder, new MailItemFolder { RemoteFolderId = "Archive" }) }))
                : sut.Delete(new BatchDeleteRequest(new[] { new DeleteRequest(copy) }));
            await bundles[0].NativeRequest.ExecuteAsync(client.Object, bundles[0].Request, CancellationToken.None);
            remote.Verify(x => x.StoreAsync(It.IsAny<IList<UniqueId>>(), It.Is<IStoreFlagsRequest>(r => r.Flags == MessageFlags.Deleted), It.IsAny<CancellationToken>()), Times.Once);
            remote.Verify(x => x.ExpungeAsync(It.IsAny<CancellationToken>()), Times.Never);
            remote.Verify(x => x.ExpungeAsync(It.IsAny<IList<UniqueId>>(), It.IsAny<CancellationToken>()), Times.Never);
            remote.Verify(x => x.MoveToAsync(It.IsAny<IList<UniqueId>>(), It.IsAny<IMailFolder>(), It.IsAny<CancellationToken>()), Times.Never);
            remote.Verify(x => x.CopyToAsync(It.IsAny<IList<UniqueId>>(), It.IsAny<IMailFolder>(), It.IsAny<CancellationToken>()), move ? Times.Once() : Times.Never());
        }
        finally { await sut.KillSynchronizerAsync(); }
    }

    [Fact]
    public async Task CanceledBatch_KeepsUnstartedRequestsInMemory()
    {
        var sut = CreateSynchronizer();
        try
        {
            var first = new MarkReadRequest(new MailCopy { UniqueId = Guid.NewGuid() }, true);
            var second = new MarkReadRequest(new MailCopy { UniqueId = Guid.NewGuid() }, true);
            sut.QueueRequest(first);
            sut.QueueRequest(second);
            var called = 0;
            var batch = new List<IRequestBundle<ImapRequest>>
            {
                new ImapRequestBundle(new ImapRequest((_, _) => throw new OperationCanceledException(), first, false), first, first),
                new ImapRequestBundle(new ImapRequest((_, _) => { called++; return Task.CompletedTask; }, second, false), second, second)
            };
            var execute = () => sut.ExecuteNativeRequestsAsync(batch);
            await execute.Should().ThrowAsync<OperationCanceledException>();
            called.Should().Be(0);
            sut.HasQueuedRequests().Should().BeTrue();
            await sut.ExecuteNativeRequestsAsync([batch[1]]);
            called.Should().Be(1);
            sut.HasQueuedRequests().Should().BeFalse();
        }
        finally { await sut.KillSynchronizerAsync(); }
    }

    [Fact]
    public async Task ConvertedArchiveAndFolderActions_KeepOriginalQueueReferences()
    {
        var sut = CreateSynchronizer();
        try
        {
            var folder = new MailItemFolder { Id = Guid.NewGuid(), RemoteFolderId = "INBOX" };
            var copy = new MailCopy { AssignedFolder = folder, FolderId = folder.Id, ImapUid = 1 };
            var archive = new ArchiveRequest(true, copy, folder, new MailItemFolder { RemoteFolderId = "Archive" });
            sut.Archive(new BatchArchiveRequest(new[] { archive }))[0].NativeRequest.QueuedRequests[0].Should().BeSameAs(archive);
            var markFolder = new MarkFolderAsReadRequest(folder, new List<MailCopy> { copy });
            sut.MarkFolderAsRead(markFolder)[0].NativeRequest.QueuedRequests[0].Should().BeSameAs(markFolder);
            var empty = new EmptyFolderRequest(folder, new List<MailCopy>());
            sut.QueueRequest(empty);
            await sut.ExecuteNativeRequestsAsync(sut.EmptyFolder(empty));
            sut.HasQueuedRequests().Should().BeFalse();
        }
        finally { await sut.KillSynchronizerAsync(); }
    }

    private static ImapSynchronizer CreateSynchronizer()
    {
        var configuration = new Mock<IApplicationConfiguration>();
        configuration.SetupGet(x => x.ApplicationDataFolderPath).Returns(Path.GetTempPath());
        var account = new MailAccount
        {
            Id = Guid.NewGuid(), ProviderType = MailProviderType.IMAP4,
            ServerInformation = new CustomServerInformation { IncomingServer = "imap.example.com", IncomingServerPort = "993", MaxConcurrentClients = 2 }
        };
        var errors = Mock.Of<IImapSynchronizerErrorHandlerFactory>();
        return new ImapSynchronizer(account, Mock.Of<IImapChangeProcessor>(), configuration.Object,
            new UnifiedImapSynchronizer(Mock.Of<IFolderService>(), Mock.Of<IMailService>(), errors), errors,
            Mock.Of<ICalDavClient>(), Mock.Of<IAutoDiscoveryService>(), Mock.Of<ICalendarService>());
    }
}
