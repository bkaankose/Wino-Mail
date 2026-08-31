using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Tests.Helpers;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Services;

public class FolderServiceTests : IAsyncLifetime
{
    private InMemoryDatabaseService _databaseService = null!;
    private FolderService _folderService = null!;
    private MailAccount _account = null!;

    public async Task InitializeAsync()
    {
        _databaseService = new InMemoryDatabaseService();
        await _databaseService.InitializeAsync();

        _account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Name = "Outlook Test",
            Address = "me@outlook.test",
            SenderName = "Test User",
            ProviderType = MailProviderType.Outlook
        };

        await _databaseService.Connection.InsertAsync(_account, typeof(MailAccount));

        var accountService = CreateAccountService(_databaseService);
        _folderService = new FolderService(_databaseService, accountService, new MailCategoryService(_databaseService));
    }

    public async Task DisposeAsync() => await _databaseService.DisposeAsync();

    [Fact]
    public async Task InsertFolderAsync_ForExistingFolder_PreservesSynchronizationState()
    {
        var folderId = Guid.NewGuid();
        var lastSynchronizedDate = DateTime.UtcNow.AddMinutes(-5);

        await _databaseService.Connection.InsertAsync(new MailItemFolder
        {
            Id = folderId,
            MailAccountId = _account.Id,
            FolderName = "Inbox",
            RemoteFolderId = "remote-inbox",
            ParentRemoteFolderId = "old-parent",
            SpecialFolderType = SpecialFolderType.Inbox,
            IsSynchronizationEnabled = true,
            DeltaToken = "https://graph.microsoft.com/v1.0/me/mailFolders/remote-inbox/messages/delta?$deltatoken=state",
            LastSynchronizedDate = lastSynchronizedDate,
            UidValidity = 42,
            HighestModeSeq = 123,
            HighestKnownUid = 456,
            LastUidReconcileUtc = DateTime.UtcNow.AddHours(-1)
        }, typeof(MailItemFolder));

        await _folderService.InsertFolderAsync(new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = _account.Id,
            FolderName = "Inbox Renamed By Server",
            RemoteFolderId = "remote-inbox",
            ParentRemoteFolderId = "new-parent",
            IsSynchronizationEnabled = true
        });

        var updatedFolder = await _databaseService.Connection.Table<MailItemFolder>()
            .FirstAsync(a => a.Id == folderId);

        updatedFolder.FolderName.Should().Be("Inbox Renamed By Server");
        updatedFolder.ParentRemoteFolderId.Should().Be("new-parent");
        updatedFolder.DeltaToken.Should().Be("https://graph.microsoft.com/v1.0/me/mailFolders/remote-inbox/messages/delta?$deltatoken=state");
        updatedFolder.LastSynchronizedDate.Should().Be(lastSynchronizedDate);
        updatedFolder.UidValidity.Should().Be(42);
        updatedFolder.HighestModeSeq.Should().Be(123);
        updatedFolder.HighestKnownUid.Should().Be(456);
        updatedFolder.LastUidReconcileUtc.Should().NotBeNull();
    }

    [Theory]
    [InlineData(MailProviderType.Gmail, "label-root", "label-child", "label-grandchild")]
    [InlineData(MailProviderType.Outlook, "graph-root", "graph-child", "graph-grandchild")]
    [InlineData(MailProviderType.IMAP4, "Projects", "Projects/2026", "Projects/2026/Wino")]
    [InlineData(MailProviderType.POP3, "local-root", "local-child", "local-grandchild")]
    public async Task GetFolderStructureForAccountAsync_BuildsCanonicalHierarchyForEveryProvider(
        MailProviderType providerType,
        string rootRemoteId,
        string childRemoteId,
        string grandchildRemoteId)
    {
        _account.ProviderType = providerType;
        await _databaseService.Connection.UpdateAsync(_account, typeof(MailAccount));

        var root = CreateFolder("Projects", rootRemoteId, isSticky: false);
        var child = CreateFolder("2026", childRemoteId, rootRemoteId, isSticky: true);
        var grandchild = CreateFolder("Wino", grandchildRemoteId, childRemoteId);

        await InsertFoldersAsync(root, grandchild, child);

        var hierarchy = await _folderService.GetFolderStructureForAccountAsync(_account.Id, true);

        hierarchy.Folders.Should().ContainSingle().Which.Id.Should().Be(root.Id);
        hierarchy.Folders[0].ChildFolders.Should().ContainSingle().Which.Id.Should().Be(child.Id);
        hierarchy.Folders[0].ChildFolders[0].ChildFolders.Should().ContainSingle().Which.Id.Should().Be(grandchild.Id);
        Flatten(hierarchy.Folders).Select(folder => folder.Id).Should().OnlyHaveUniqueItems().And.HaveCount(3);
        Flatten(hierarchy.Folders).Should().NotContain(folder => folder.SpecialFolderType == SpecialFolderType.More);
    }

    [Fact]
    public async Task GetFolderStructureForAccountAsync_SortsEverySiblingLevel()
    {
        var inbox = CreateFolder("Inbox", "inbox", specialFolderType: SpecialFolderType.Inbox);
        var rootZ = CreateFolder("Zulu", "root-z");
        var rootA = CreateFolder("Alpha", "root-a");
        var childZ = CreateFolder("Zulu child", "child-z", rootA.RemoteFolderId);
        var childA = CreateFolder("Alpha child", "child-a", rootA.RemoteFolderId);

        await InsertFoldersAsync(rootZ, childZ, rootA, inbox, childA);

        var hierarchy = await _folderService.GetFolderStructureForAccountAsync(_account.Id, true);

        hierarchy.Folders.Select(folder => folder.FolderName).Should().Equal("Inbox", "Alpha", "Zulu");
        hierarchy.Folders[1].ChildFolders.Select(folder => folder.FolderName).Should().Equal("Alpha child", "Zulu child");
    }

    [Fact]
    public async Task GetFolderStructureForAccountAsync_PreservesFlattenedImapInboxChildren()
    {
        _account.ProviderType = MailProviderType.IMAP4;
        await _databaseService.Connection.UpdateAsync(_account, typeof(MailAccount));

        var inbox = CreateFolder("Inbox", "INBOX", specialFolderType: SpecialFolderType.Inbox);
        var flattenedInboxChild = CreateFolder("Receipts", "INBOX/Receipts");

        await InsertFoldersAsync(inbox, flattenedInboxChild);

        var hierarchy = await _folderService.GetFolderStructureForAccountAsync(_account.Id, true);

        hierarchy.Folders.Select(folder => folder.Id).Should().BeEquivalentTo(
            [inbox.Id, flattenedInboxChild.Id]);
        inbox.ChildFolders.Should().BeEmpty();
    }

    [Fact]
    public async Task GetFolderStructureForAccountAsync_RespectsHiddenFoldersAtEveryDepth()
    {
        var hiddenParent = CreateFolder("Hidden parent", "hidden-parent", isHidden: true);
        var visibleChild = CreateFolder("Visible child", "visible-child", hiddenParent.RemoteFolderId);

        await InsertFoldersAsync(hiddenParent, visibleChild);

        var visibleHierarchy = await _folderService.GetFolderStructureForAccountAsync(_account.Id, false);
        var completeHierarchy = await _folderService.GetFolderStructureForAccountAsync(_account.Id, true);

        visibleHierarchy.Folders.Should().ContainSingle().Which.Id.Should().Be(visibleChild.Id);
        completeHierarchy.Folders.Should().ContainSingle().Which.Id.Should().Be(hiddenParent.Id);
        completeHierarchy.Folders[0].ChildFolders.Should().ContainSingle().Which.Id.Should().Be(visibleChild.Id);
    }

    [Fact]
    public async Task GetFolderStructureForAccountAsync_PromotesInvalidParentLinksWithoutLosingFolders()
    {
        var cycleA = CreateFolder("Cycle A", "cycle-a", "cycle-b");
        var cycleB = CreateFolder("Cycle B", "cycle-b", "cycle-a");
        var selfParent = CreateFolder("Self", "self", "self");
        var orphan = CreateFolder("Orphan", "orphan", "missing-parent");

        await InsertFoldersAsync(cycleA, cycleB, selfParent, orphan);

        var hierarchy = await _folderService.GetFolderStructureForAccountAsync(_account.Id, true);
        var flattened = Flatten(hierarchy.Folders).ToList();

        flattened.Select(folder => folder.Id).Should().OnlyHaveUniqueItems().And.BeEquivalentTo(
            [cycleA.Id, cycleB.Id, selfParent.Id, orphan.Id]);
        hierarchy.Folders.Should().Contain(folder => folder.Id == selfParent.Id);
        hierarchy.Folders.Should().Contain(folder => folder.Id == orphan.Id);
    }

    private MailItemFolder CreateFolder(
        string name,
        string remoteId,
        string? parentRemoteId = null,
        bool isSticky = false,
        bool isHidden = false,
        SpecialFolderType specialFolderType = SpecialFolderType.Other)
        => new()
        {
            Id = Guid.NewGuid(),
            MailAccountId = _account.Id,
            FolderName = name,
            RemoteFolderId = remoteId,
            ParentRemoteFolderId = parentRemoteId ?? string.Empty,
            IsSticky = isSticky,
            IsHidden = isHidden,
            SpecialFolderType = specialFolderType
        };

    private async Task InsertFoldersAsync(params MailItemFolder[] folders)
    {
        foreach (var folder in folders)
        {
            await _databaseService.Connection.InsertAsync(folder, typeof(MailItemFolder));
        }
    }

    private static IEnumerable<IMailItemFolder> Flatten(IEnumerable<IMailItemFolder> folders)
    {
        foreach (var folder in folders)
        {
            yield return folder;

            foreach (var childFolder in Flatten(folder.ChildFolders))
            {
                yield return childFolder;
            }
        }
    }

    private static AccountService CreateAccountService(InMemoryDatabaseService databaseService)
    {
        var signatureService = new Mock<ISignatureService>();
        signatureService
            .Setup(a => a.CreateDefaultSignatureAsync(It.IsAny<Guid>()))
            .ReturnsAsync((Guid accountId) => new AccountSignature
            {
                Id = Guid.NewGuid(),
                MailAccountId = accountId,
                Name = "Default",
                HtmlBody = string.Empty
            });

        return new AccountService(
            databaseService,
            signatureService.Object,
            Mock.Of<IAuthenticationProvider>(),
            Mock.Of<IMimeFileService>(),
            Mock.Of<IPreferencesService>(),
            Mock.Of<IContactPictureFileService>());
    }
}
