using FluentAssertions;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.PublicFolders;
using Xunit;

namespace Wino.Core.Tests.PublicFolders;

public class RemoteFolderMenuItemTests
{
    private readonly MailAccount _account = new() { Id = Guid.NewGuid(), Name = "Exchange", ProviderType = MailProviderType.Exchange };

    [Fact]
    public void Roots_AreStablePerAccountAndCarryAPlaceholder()
    {
        var first = PublicFolderMenuItemFactory.CreatePublicFoldersRoot(_account, null);
        var second = PublicFolderMenuItemFactory.CreatePublicFoldersRoot(_account, null);
        var archive = PublicFolderMenuItemFactory.CreateOnlineArchiveRoot(_account, null);

        first.RemoteFolder.Id.Should().Be(second.RemoteFolder.Id);
        first.RemoteFolder.Id.Should().NotBe(archive.RemoteFolder.Id);
        first.IsRoot.Should().BeTrue();
        first.CanOpen.Should().BeFalse();
        first.CanPin.Should().BeFalse();
        first.AreChildrenLoaded.Should().BeFalse();
        first.SubMenuItems.Should().ContainSingle().Which.Should().Match<RemoteFolderMenuItem>(p => p.IsPlaceholder);
        archive.IsOnlineArchive.Should().BeTrue();
        archive.SpecialFolderType.Should().Be(SpecialFolderType.OnlineArchive);
    }

    [Fact]
    public void Nodes_AreNeverMoveTargetsAndOnlyMailFoldersOpen()
    {
        var root = PublicFolderMenuItemFactory.CreatePublicFoldersRoot(_account, null);
        var mail = PublicFolderMenuItemFactory.CreateNode(_account, new PublicFolderNode { Id = "f1", Name = "Sales", Kind = PublicFolderKind.Mail, HasChildren = true }, root);
        var contacts = PublicFolderMenuItemFactory.CreateNode(_account, new PublicFolderNode { Id = "f2", Name = "People", Kind = PublicFolderKind.Contacts }, root);

        mail.IsMoveTarget.Should().BeFalse();
        mail.CanOpen.Should().BeTrue();
        mail.CanPin.Should().BeTrue();
        mail.AreChildrenLoaded.Should().BeFalse();
        mail.SubMenuItems.Should().ContainSingle();

        contacts.CanOpen.Should().BeFalse();
        contacts.AreChildrenLoaded.Should().BeTrue();
        contacts.SubMenuItems.Should().BeEmpty();
        contacts.RemoteFolder.RemoteFolderId.Should().Be("f2");
    }

    [Theory]
    [InlineData(PublicFolderKind.Mail, true)]
    [InlineData(PublicFolderKind.Contacts, true)]
    [InlineData(PublicFolderKind.Calendar, true)]
    [InlineData(PublicFolderKind.Container, false)]
    [InlineData(PublicFolderKind.Other, false)]
    public void OnlyKindsWithAHome_CanBePinned(PublicFolderKind kind, bool expected)
    {
        var root = PublicFolderMenuItemFactory.CreatePublicFoldersRoot(_account, null);
        var node = PublicFolderMenuItemFactory.CreateNode(_account, new PublicFolderNode { Id = "f1", Name = "Folder", Kind = kind }, root);

        node.CanPin.Should().Be(expected);
    }

    [Fact]
    public void PinActionText_NamesWhereTheFolderWillSurface()
    {
        var root = PublicFolderMenuItemFactory.CreatePublicFoldersRoot(_account, null);
        var texts = new[] { PublicFolderKind.Mail, PublicFolderKind.Contacts, PublicFolderKind.Calendar }
            .Select(kind => PublicFolderMenuItemFactory.CreateNode(_account, new PublicFolderNode { Id = kind.ToString(), Name = "Folder", Kind = kind }, root))
            .ToList();

        texts.Select(node => node.PinActionText).Should().OnlyHaveUniqueItems();

        var changed = new List<string>();
        texts[1].PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        texts[1].IsPinned = true;

        changed.Should().Contain(nameof(RemoteFolderMenuItem.PinActionText));
        texts[1].PinActionText.Should().Be(Wino.Core.Domain.Translator.PublicFolders_Unpin);
    }

    [Fact]
    public void ArchiveChildren_InheritTheArchiveKindFromTheirParent()
    {
        var root = PublicFolderMenuItemFactory.CreateOnlineArchiveRoot(_account, null);
        var child = PublicFolderMenuItemFactory.CreateNode(_account, new PublicFolderNode { Id = "a1", Name = "2019", Kind = PublicFolderKind.Mail }, root);

        child.IsOnlineArchive.Should().BeTrue();
        child.RemoteFolder.IsOnlineArchiveNode.Should().BeTrue();
        child.RemoteFolder.IsPublicFolderNode.Should().BeFalse();
        child.CanPin.Should().BeFalse("only public folders can be pinned");
        child.CanOpen.Should().BeTrue();
    }

    [Fact]
    public void Expanding_RequestsChildrenOnceUntilTheyAreLoaded()
    {
        var root = PublicFolderMenuItemFactory.CreatePublicFoldersRoot(_account, null);
        var requests = 0;
        root.ChildrenRequested += (_, _) => requests++;

        root.IsExpanded = true;
        root.IsExpanded = false;
        root.IsExpanded = true;
        requests.Should().Be(2);

        root.MarkChildrenLoaded();
        root.IsExpanded = false;
        root.IsExpanded = true;
        requests.Should().Be(2);
    }

    [Fact]
    public void Placeholders_NeverRequestChildren()
    {
        var root = PublicFolderMenuItemFactory.CreatePublicFoldersRoot(_account, null);
        var placeholder = (RemoteFolderMenuItem)root.SubMenuItems[0];
        var requests = 0;
        placeholder.ChildrenRequested += (_, _) => requests++;

        placeholder.IsExpanded = true;

        requests.Should().Be(0);
        PublicFolderMenuItemFactory.IsPlaceholder(placeholder).Should().BeTrue();
        PublicFolderMenuItemFactory.IsPlaceholder(root).Should().BeFalse();
    }

    [Fact]
    public void PinnedEntry_IsStableAndReportsItselfPinned()
    {
        var favorite = new PublicFolderFavorite { AccountId = _account.Id, FolderId = "f1", Kind = PublicFolderKind.Mail, Name = "Sales" };

        var first = PublicFolderMenuItemFactory.CreatePinnedMailFolder(_account, favorite, null);
        var second = PublicFolderMenuItemFactory.CreatePinnedMailFolder(_account, favorite, null);

        first.RemoteFolder.Id.Should().Be(second.RemoteFolder.Id);
        first.IsPinnedEntry.Should().BeTrue();
        first.IsPinned.Should().BeTrue();
        first.CanOpen.Should().BeTrue();
        first.RemoteFolder.RemoteFolderId.Should().Be("f1");
        first.FolderName.Should().StartWith("Sales");
    }

    [Fact]
    public void TogglePin_RaisesThePinRequest()
    {
        var root = PublicFolderMenuItemFactory.CreatePublicFoldersRoot(_account, null);
        var node = PublicFolderMenuItemFactory.CreateNode(_account, new PublicFolderNode { Id = "f1", Name = "Sales", Kind = PublicFolderKind.Mail }, root);
        RemoteFolderMenuItem raised = null;
        node.PinToggleRequested += (sender, _) => raised = (RemoteFolderMenuItem)sender;

        node.TogglePinCommand.Execute(null);

        raised.Should().BeSameAs(node);
    }
}
