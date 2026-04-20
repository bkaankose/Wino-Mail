using FluentAssertions;
using MailKit;
using MailKit.Net.Imap;
using Moq;
using System.Linq;
using System.Reflection;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Synchronizers.ImapSync;
using Wino.Services.Extensions;
using Xunit;
using IMailService = Wino.Core.Domain.Interfaces.IMailService;

namespace Wino.Core.Tests.Synchronizers;

public class UnifiedImapSynchronizerTests
{
    private static UnifiedImapSynchronizer CreateSut()
    {
        return new UnifiedImapSynchronizer(
            Mock.Of<IFolderService>(),
            Mock.Of<IMailService>(),
            Mock.Of<IImapSynchronizerErrorHandlerFactory>());
    }

    [Fact]
    public void DetermineSyncStrategy_ShouldPrioritizeQResync_WhenEnabledAndSupported()
    {
        var sut = CreateSut();

        var strategy = sut.DetermineSyncStrategy(
            ImapCapabilities.QuickResync | ImapCapabilities.CondStore,
            isQResyncEnabled: true,
            serverHost: "imap.example.com");

        strategy.Should().Be(ImapSyncStrategy.QResync);
    }

    [Fact]
    public void DetermineSyncStrategy_ShouldFallbackToCondstore_WhenQResyncNotEnabled()
    {
        var sut = CreateSut();

        var strategy = sut.DetermineSyncStrategy(
            ImapCapabilities.QuickResync | ImapCapabilities.CondStore,
            isQResyncEnabled: false,
            serverHost: "imap.example.com");

        strategy.Should().Be(ImapSyncStrategy.Condstore);
    }

    [Fact]
    public void DetermineSyncStrategy_ShouldUseUidFallback_WhenNoAdvancedCapability()
    {
        var sut = CreateSut();

        var strategy = sut.DetermineSyncStrategy(
            ImapCapabilities.None,
            isQResyncEnabled: false,
            serverHost: "imap.example.com");

        strategy.Should().Be(ImapSyncStrategy.UidBased);
    }

    [Fact]
    public void DetermineSyncStrategy_ShouldRespectQuirkOverride_ForStrictProviders()
    {
        var sut = CreateSut();

        var strategy = sut.DetermineSyncStrategy(
            ImapCapabilities.QuickResync | ImapCapabilities.CondStore,
            isQResyncEnabled: true,
            serverHost: "imap.qq.com");

        strategy.Should().Be(ImapSyncStrategy.Condstore);
    }

    [Fact]
    public void DetermineSyncStrategy_ShouldFallbackToUid_WhenCondstoreIsUnavailable()
    {
        var sut = CreateSut();

        var strategy = sut.DetermineSyncStrategy(
            ImapCapabilities.QuickResync,
            isQResyncEnabled: false,
            serverHost: "imap.example.com");

        strategy.Should().Be(ImapSyncStrategy.UidBased);
    }

    [Fact]
    public void DetermineSyncStrategy_ShouldFallbackToUid_WhenQuirkDisablesQresyncAndNoCondstore()
    {
        var sut = CreateSut();

        var strategy = sut.DetermineSyncStrategy(
            ImapCapabilities.QuickResync,
            isQResyncEnabled: true,
            serverHost: "imap.163.com");

        strategy.Should().Be(ImapSyncStrategy.UidBased);
    }

    [Fact]
    public void CalculateHighestKnownUid_ShouldUseMaxOfCurrentObservedAndUidNext()
    {
        var result = UnifiedImapSynchronizer.CalculateHighestKnownUid(
            currentHighestKnownUid: 100,
            uidNext: new MailKit.UniqueId(151),
            observedUids: new uint[] { 120, 140, 130 });

        result.Should().Be(150);
    }

    [Fact]
    public void CalculateHighestKnownUid_ShouldNotRegress_WhenObservedUidsAreLower()
    {
        var result = UnifiedImapSynchronizer.CalculateHighestKnownUid(
            currentHighestKnownUid: 500,
            uidNext: null,
            observedUids: new uint[] { 110, 120, 130 });

        result.Should().Be(500);
    }

    [Fact]
    public void CalculateHighestKnownUid_ShouldUseUidNextMinusOne_WhenNoObservedUids()
    {
        var result = UnifiedImapSynchronizer.CalculateHighestKnownUid(
            currentHighestKnownUid: 0,
            uidNext: new MailKit.UniqueId(901),
            observedUids: null);

        result.Should().Be(900);
    }

    [Fact]
    public void ShouldRunUidReconcile_ShouldReturnTrue_WhenNeverReconciled()
    {
        var shouldRun = UnifiedImapSynchronizer.ShouldRunUidReconcile(
            lastUidReconcileUtc: null,
            utcNow: DateTime.UtcNow,
            reconcileInterval: TimeSpan.FromHours(12));

        shouldRun.Should().BeTrue();
    }

    [Fact]
    public void ShouldRunUidReconcile_ShouldReturnFalse_WhenWithinInterval()
    {
        var now = DateTime.UtcNow;

        var shouldRun = UnifiedImapSynchronizer.ShouldRunUidReconcile(
            lastUidReconcileUtc: now.AddHours(-1),
            utcNow: now,
            reconcileInterval: TimeSpan.FromHours(12));

        shouldRun.Should().BeFalse();
    }

    [Fact]
    public void ShouldRunUidReconcile_ShouldReturnTrue_WhenIntervalElapsed()
    {
        var now = DateTime.UtcNow;

        var shouldRun = UnifiedImapSynchronizer.ShouldRunUidReconcile(
            lastUidReconcileUtc: now.AddHours(-13),
            utcNow: now,
            reconcileInterval: TimeSpan.FromHours(12));

        shouldRun.Should().BeTrue();
    }

    [Fact]
    public async Task ProcessSummariesAsync_ShouldUseMetadataOnlyPackage()
    {
        var localFolder = new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = Guid.NewGuid(),
            FolderName = "Inbox",
            RemoteFolderId = "INBOX"
        };

        var summaryMock = new Mock<IMessageSummary>();
        summaryMock.SetupGet(x => x.UniqueId).Returns(new UniqueId(42));
        summaryMock.SetupGet(x => x.Flags).Returns(MessageFlags.None);

        var mailServiceMock = new Mock<IMailService>();
        mailServiceMock
            .Setup(x => x.GetExistingMailsAsync(localFolder.Id, It.IsAny<IEnumerable<UniqueId>>()))
            .ReturnsAsync(new List<MailCopy>());
        mailServiceMock
            .Setup(x => x.CreateMailAsync(localFolder.MailAccountId, It.IsAny<NewMailItemPackage>()))
            .ReturnsAsync(true);

        var sut = new UnifiedImapSynchronizer(
            Mock.Of<IFolderService>(),
            mailServiceMock.Object,
            Mock.Of<IImapSynchronizerErrorHandlerFactory>());

        ImapMessageCreationPackage? capturedPackage = null;

        var imapSynchronizerMock = new Mock<IImapSynchronizer>();
        imapSynchronizerMock
            .Setup(x => x.CreateNewMailPackagesAsync(It.IsAny<ImapMessageCreationPackage>(), localFolder, It.IsAny<CancellationToken>()))
            .Callback<ImapMessageCreationPackage, MailItemFolder, CancellationToken>((package, _, _) => capturedPackage = package)
            .ReturnsAsync(new List<NewMailItemPackage>
            {
                new(new MailCopy { Id = "mail-id" }, null, localFolder.RemoteFolderId, Array.Empty<AccountContact>())
            });

        var processMethod = typeof(UnifiedImapSynchronizer).GetMethod("ProcessSummariesAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        processMethod.Should().NotBeNull();

        var task = (Task<List<string>>)processMethod!.Invoke(
            sut,
            [imapSynchronizerMock.Object, localFolder, new List<IMessageSummary> { summaryMock.Object }, CancellationToken.None])!;

        var result = await task;

        result.Should().ContainSingle().Which.Should().Be("mail-id");
        capturedPackage.Should().NotBeNull();
        capturedPackage!.MimeMessage.Should().BeNull();
    }

    [Fact]
    public async Task ProcessSummariesAsync_ShouldBatchStateUpdates_ForExistingMailCopies()
    {
        var localFolder = new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = Guid.NewGuid(),
            FolderName = "Inbox",
            RemoteFolderId = "INBOX"
        };

        var summaryMock = new Mock<IMessageSummary>();
        summaryMock.SetupGet(x => x.UniqueId).Returns(new UniqueId(42));
        summaryMock.SetupGet(x => x.Flags).Returns(MessageFlags.Seen | MessageFlags.Flagged);

        var existingMailCopy = new MailCopy
        {
            Id = MailkitClientExtensions.CreateUid(localFolder.Id, 42),
            IsRead = false,
            IsFlagged = false
        };

        var mailServiceMock = new Mock<IMailService>();
        mailServiceMock
            .Setup(x => x.GetExistingMailsAsync(localFolder.Id, It.IsAny<IEnumerable<UniqueId>>()))
            .ReturnsAsync([existingMailCopy]);
        mailServiceMock
            .Setup(x => x.ApplyMailStateUpdatesAsync(It.IsAny<IEnumerable<MailCopyStateUpdate>>()))
            .Returns(Task.CompletedTask);

        var sut = new UnifiedImapSynchronizer(
            Mock.Of<IFolderService>(),
            mailServiceMock.Object,
            Mock.Of<IImapSynchronizerErrorHandlerFactory>());

        var imapSynchronizerMock = new Mock<IImapSynchronizer>();

        var processMethod = typeof(UnifiedImapSynchronizer).GetMethod("ProcessSummariesAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        processMethod.Should().NotBeNull();

        var task = (Task<List<string>>)processMethod!.Invoke(
            sut,
            [imapSynchronizerMock.Object, localFolder, new List<IMessageSummary> { summaryMock.Object }, CancellationToken.None])!;

        var result = await task;

        result.Should().BeEmpty();
        mailServiceMock.Verify(
            x => x.ApplyMailStateUpdatesAsync(It.Is<IEnumerable<MailCopyStateUpdate>>(updates =>
                updates.Count() == 1
                && updates.First().MailCopyId == existingMailCopy.Id
                && updates.First().IsRead == true
                && updates.First().IsFlagged == true)),
            Times.Once);
        mailServiceMock.Verify(x => x.CreateMailAsync(It.IsAny<Guid>(), It.IsAny<NewMailItemPackage>()), Times.Never);
    }
}
