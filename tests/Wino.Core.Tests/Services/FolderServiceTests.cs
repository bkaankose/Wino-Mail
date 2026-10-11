using FluentAssertions;
using Moq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
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
    public async Task GetFolderUnreadCountAsync_WithFocusedFilter_CountsRequestedSection()
    {
        var inbox = CreateFolder("Inbox", "inbox", specialFolderType: SpecialFolderType.Inbox);
        await InsertFoldersAsync(inbox);

        var messages = new MailCopy[]
        {
            new MailCopy { UniqueId = Guid.NewGuid(), Id = "focused-unread", FolderId = inbox.Id, IsFocused = true, IsRead = false },
            new MailCopy { UniqueId = Guid.NewGuid(), Id = "other-unread", FolderId = inbox.Id, IsFocused = false, IsRead = false },
            new MailCopy { UniqueId = Guid.NewGuid(), Id = "other-read", FolderId = inbox.Id, IsFocused = false, IsRead = true }
        };

        foreach (var message in messages)
        {
            await _databaseService.Connection.InsertAsync(message, typeof(MailCopy));
        }

        var focusedUnreadCount = await _folderService.GetFolderUnreadCountAsync(inbox.Id, true);
        var otherUnreadCount = await _folderService.GetFolderUnreadCountAsync(inbox.Id, false);

        focusedUnreadCount.Should().Be(1);
        otherUnreadCount.Should().Be(1);
    }

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
    public async Task GetFolderStructureForAccountAsync_HiddenParentHidesUnpinnedDescendants()
    {
        var hiddenParent = CreateFolder("Hidden parent", "hidden-parent", isHidden: true);
        var child = CreateFolder("Child", "child", hiddenParent.RemoteFolderId);
        var grandchild = CreateFolder("Grandchild", "grandchild", child.RemoteFolderId);
        var pinnedChild = CreateFolder("Pinned child", "pinned-child", hiddenParent.RemoteFolderId, isSticky: true);
        var pinnedChildChild = CreateFolder("Pinned child child", "pinned-child-child", pinnedChild.RemoteFolderId);

        await InsertFoldersAsync(hiddenParent, child, grandchild, pinnedChild, pinnedChildChild);

        var visibleHierarchy = await _folderService.GetFolderStructureForAccountAsync(_account.Id, false);
        var completeHierarchy = await _folderService.GetFolderStructureForAccountAsync(_account.Id, true);

        visibleHierarchy.Folders.Should().ContainSingle().Which.Id.Should().Be(pinnedChild.Id);
        visibleHierarchy.Folders[0].ChildFolders.Should().ContainSingle().Which.Id.Should().Be(pinnedChildChild.Id);
        Flatten(completeHierarchy.Folders).Select(folder => folder.Id).Should().OnlyHaveUniqueItems().And.HaveCount(5);
        completeHierarchy.Folders.Should().ContainSingle().Which.Id.Should().Be(hiddenParent.Id);
    }

    [Fact]
    public async Task GetVisibleFoldersAsync_HiddenParentHidesUnpinnedDescendants()
    {
        var hiddenParent = CreateFolder("Hidden parent", "hidden-parent", isHidden: true);
        var child = CreateFolder("Child", "child", hiddenParent.RemoteFolderId);
        var pinnedChild = CreateFolder("Pinned child", "pinned-child", hiddenParent.RemoteFolderId, isSticky: true);
        var hiddenPinnedChild = CreateFolder("Hidden pinned", "hidden-pinned", hiddenParent.RemoteFolderId, isSticky: true, isHidden: true);
        var orphan = CreateFolder("Orphan", "orphan", "missing-parent");

        await InsertFoldersAsync(hiddenParent, child, pinnedChild, hiddenPinnedChild, orphan);

        var visibleFolders = await _folderService.GetVisibleFoldersAsync(_account.Id);

        visibleFolders.Select(folder => folder.Id).Should().BeEquivalentTo([pinnedChild.Id, orphan.Id]);
    }

    [Theory]
    [InlineData(MailProviderType.IMAP4, "Projects", "Projects/2026", "Projects/2026/Wino")]
    [InlineData(MailProviderType.Gmail, "label-root", "label-child", "label-grandchild")]
    [InlineData(MailProviderType.Outlook, "graph-root", "graph-child", "graph-grandchild")]
    public async Task GetAccountFoldersForDisplayAsync_NestedFolders_AreListedOnceUnderTheirParent(
        MailProviderType providerType,
        string rootRemoteId,
        string childRemoteId,
        string grandchildRemoteId)
    {
        _account.ProviderType = providerType;
        await _databaseService.Connection.UpdateAsync(_account, typeof(MailAccount));

        var inbox = CreateFolder("Inbox", "INBOX", isSticky: true, specialFolderType: SpecialFolderType.Inbox);
        var root = CreateFolder("Projects", rootRemoteId);
        var child = CreateFolder("2026", childRemoteId, rootRemoteId);
        var grandchild = CreateFolder("Wino", grandchildRemoteId, childRemoteId);

        await InsertFoldersAsync(inbox, grandchild, child, root);

        var menuItems = (await _folderService.GetAccountFoldersForDisplayAsync(new AccountMenuItem(_account, null))).ToList();
        var flattened = FlattenMenu(menuItems).ToList();

        flattened.Select(item => item.EntityId).Should().OnlyHaveUniqueItems();
        flattened.Where(item => item.SpecialFolderType != SpecialFolderType.More).Should().HaveCount(4);

        var more = menuItems.OfType<FolderMenuItem>().Should().ContainSingle(item => item.SpecialFolderType == SpecialFolderType.More).Subject;
        var rootItem = more.SubMenuItems.Cast<FolderMenuItem>().Should().ContainSingle().Subject;
        rootItem.EntityId.Should().Be(root.Id);
        var childItem = rootItem.SubMenuItems.Cast<FolderMenuItem>().Should().ContainSingle().Subject;
        childItem.EntityId.Should().Be(child.Id);
        childItem.SubMenuItems.Cast<FolderMenuItem>().Should().ContainSingle().Which.EntityId.Should().Be(grandchild.Id);
    }

    [Fact]
    public async Task GetFolderStructureForDisplayAsync_NestedImapFolders_AreListedOnceUnderTheirParent()
    {
        _account.ProviderType = MailProviderType.IMAP4;
        await _databaseService.Connection.UpdateAsync(_account, typeof(MailAccount));

        var inbox = CreateFolder("Inbox", "INBOX", isSticky: true, specialFolderType: SpecialFolderType.Inbox);
        var root = CreateFolder("Projects", "Projects");
        var child = CreateFolder("2026", "Projects/2026", root.RemoteFolderId);
        var grandchild = CreateFolder("Wino", "Projects/2026/Wino", child.RemoteFolderId);

        await InsertFoldersAsync(inbox, grandchild, child, root);

        var structure = await _folderService.GetFolderStructureForDisplayAsync(_account.Id);

        Flatten(structure).Where(folder => folder.IsMoveTarget).Select(folder => folder.Id).Should()
            .OnlyHaveUniqueItems().And.BeEquivalentTo([inbox.Id, root.Id, child.Id, grandchild.Id]);
        var more = structure.Should().ContainSingle(folder => folder.SpecialFolderType == SpecialFolderType.More).Subject;
        more.ChildFolders.Should().ContainSingle().Which.Id.Should().Be(root.Id);
    }

    [Fact]
    public async Task GetAccountFoldersForDisplayAsync_PinnedChild_IsListedOnceAtTheRoot()
    {
        _account.ProviderType = MailProviderType.IMAP4;
        await _databaseService.Connection.UpdateAsync(_account, typeof(MailAccount));

        var root = CreateFolder("Projects", "Projects");
        var pinnedChild = CreateFolder("2026", "Projects/2026", root.RemoteFolderId, isSticky: true);

        await InsertFoldersAsync(root, pinnedChild);

        var menuItems = (await _folderService.GetAccountFoldersForDisplayAsync(new AccountMenuItem(_account, null))).ToList();

        FlattenMenu(menuItems).Select(item => item.EntityId).Should().OnlyHaveUniqueItems();
        menuItems.OfType<FolderMenuItem>().Should().Contain(item => item.EntityId == pinnedChild.Id);
        var more = menuItems.OfType<FolderMenuItem>().Single(item => item.SpecialFolderType == SpecialFolderType.More);
        more.SubMenuItems.Cast<FolderMenuItem>().Should().ContainSingle().Which.SubMenuItems.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAccountFoldersForDisplayAsync_HiddenParent_HidesUnpinnedChildrenEverywhere()
    {
        _account.ProviderType = MailProviderType.IMAP4;
        await _databaseService.Connection.UpdateAsync(_account, typeof(MailAccount));

        var hiddenParent = CreateFolder("Archive 2020", "Archive 2020", isHidden: true);
        var child = CreateFolder("January", "Archive 2020/January", hiddenParent.RemoteFolderId);
        var pinnedChild = CreateFolder("Taxes", "Archive 2020/Taxes", hiddenParent.RemoteFolderId, isSticky: true);

        await InsertFoldersAsync(hiddenParent, child, pinnedChild);

        var menuItems = (await _folderService.GetAccountFoldersForDisplayAsync(new AccountMenuItem(_account, null))).ToList();
        var structure = await _folderService.GetFolderStructureForDisplayAsync(_account.Id);

        FlattenMenu(menuItems).Select(item => item.EntityId).Should().Equal(pinnedChild.Id);
        Flatten(structure).Select(folder => folder.Id).Should().Equal(pinnedChild.Id);
    }

    [Fact]
    public async Task GetAccountFoldersForDisplayAsync_InvalidParentLinks_KeepEveryFolderOnce()
    {
        _account.ProviderType = MailProviderType.IMAP4;
        await _databaseService.Connection.UpdateAsync(_account, typeof(MailAccount));

        var cycleA = CreateFolder("Cycle A", "cycle-a", "cycle-b");
        var cycleB = CreateFolder("Cycle B", "cycle-b", "cycle-a");
        var selfParent = CreateFolder("Self", "self", "self");
        var orphan = CreateFolder("Orphan", "[Gmail]/Orphan", "[Gmail]");

        await InsertFoldersAsync(cycleA, cycleB, selfParent, orphan);

        var menuItems = await _folderService.GetAccountFoldersForDisplayAsync(new AccountMenuItem(_account, null));

        FlattenMenu(menuItems)
            .Where(item => item.SpecialFolderType != SpecialFolderType.More)
            .Select(item => item.EntityId)
            .Should().OnlyHaveUniqueItems().And.BeEquivalentTo([cycleA.Id, cycleB.Id, selfParent.Id, orphan.Id]);
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

    [Fact]
    public async Task GetAccountFoldersForDisplayAsync_AllGmailCategoriesHidden_OmitsCategoriesMenuItem()
    {
        _account.ProviderType = MailProviderType.Gmail;
        await _databaseService.Connection.UpdateAsync(_account, typeof(MailAccount));

        var inbox = CreateFolder("Inbox", ServiceConstants.INBOX_LABEL_ID, specialFolderType: SpecialFolderType.Inbox);
        var hiddenPromotions = CreateFolder(
            "Promotions",
            $"{ServiceConstants.CATEGORY_PREFIX}{ServiceConstants.PROMOTIONS_LABEL_ID}",
            isHidden: true,
            specialFolderType: SpecialFolderType.Promotions);

        await InsertFoldersAsync(inbox, hiddenPromotions);

        var accountMenuItem = new AccountMenuItem(_account, null);
        var menuItems = await _folderService.GetAccountFoldersForDisplayAsync(accountMenuItem);

        menuItems
            .OfType<FolderMenuItem>()
            .Should().NotContain(folderMenuItem => folderMenuItem.SpecialFolderType == SpecialFolderType.Category);
    }

    [Fact]
    public async Task GetAccountFoldersForDisplayAsync_VisibleGmailCategory_ShowsCategoriesMenuItem()
    {
        _account.ProviderType = MailProviderType.Gmail;
        await _databaseService.Connection.UpdateAsync(_account, typeof(MailAccount));

        var promotions = CreateFolder(
            "Promotions",
            $"{ServiceConstants.CATEGORY_PREFIX}{ServiceConstants.PROMOTIONS_LABEL_ID}",
            specialFolderType: SpecialFolderType.Promotions);

        await InsertFoldersAsync(promotions);

        var accountMenuItem = new AccountMenuItem(_account, null);
        var menuItems = await _folderService.GetAccountFoldersForDisplayAsync(accountMenuItem);

        var categoriesMenuItem = menuItems
            .OfType<FolderMenuItem>()
            .Should().ContainSingle(item => item.SpecialFolderType == SpecialFolderType.Category)
            .Subject;
        categoriesMenuItem.SubMenuItems.Should().ContainSingle();
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

    private static IEnumerable<FolderMenuItem> FlattenMenu(IEnumerable<IMenuItem> menuItems)
    {
        foreach (var menuItem in menuItems.OfType<FolderMenuItem>())
        {
            yield return menuItem;

            foreach (var childMenuItem in FlattenMenu(menuItem.SubMenuItems))
            {
                yield return childMenuItem;
            }
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
            Mock.Of<IPictureStorageService>(),
            credentialPersistence: Wino.Core.Tests.Helpers.TestAccountCredentialPersistence.Instance);
    }
}
