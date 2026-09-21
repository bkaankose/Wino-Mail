using System;
using System.Security.Cryptography;
using System.Text;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PublicFolders;

namespace Wino.Core.Domain.MenuItems;

/// <summary>
/// Builds the read-only remote tree nodes of the mail navigation: the "Public Folders" and "Online Archive"
/// roots under an Exchange account, their lazily loaded children, pinned quick-access entries and the inert
/// placeholder rows. Every node wraps a synthetic, never-persisted <see cref="MailItemFolder"/>.
/// </summary>
public static class PublicFolderMenuItemFactory
{
    /// <summary>The "Public Folders" root shown under an Exchange account, with a placeholder so it expands.</summary>
    public static RemoteFolderMenuItem CreatePublicFoldersRoot(MailAccount account, IMenuItem parent)
    {
        var folder = new MailItemFolder
        {
            // Stable per account so the node keeps its identity across menu rebuilds.
            Id = DeterministicId(account.Id, "public-folders-root"),
            MailAccountId = account.Id,
            FolderName = Translator.PublicFolders_RootName,
            SpecialFolderType = SpecialFolderType.PublicFolders,
            IsSticky = true,
            IsPublicFolderNode = true,
            PublicFolderKind = PublicFolderKind.Container,
        };

        var item = new RemoteFolderMenuItem(folder, account, parent);
        item.SubMenuItems.Add(CreatePlaceholder(account, item, Translator.RemoteFolders_Loading, isOnlineArchive: false));
        return item;
    }

    /// <summary>The "Online Archive" root shown under an Exchange account, with a placeholder so it expands.</summary>
    public static RemoteFolderMenuItem CreateOnlineArchiveRoot(MailAccount account, IMenuItem parent)
    {
        var folder = new MailItemFolder
        {
            Id = DeterministicId(account.Id, "online-archive-root"),
            MailAccountId = account.Id,
            FolderName = Translator.OnlineArchive_RootName,
            SpecialFolderType = SpecialFolderType.OnlineArchive,
            IsSticky = true,
            IsOnlineArchiveNode = true,
            PublicFolderKind = PublicFolderKind.Container,
        };

        var item = new RemoteFolderMenuItem(folder, account, parent);
        item.SubMenuItems.Add(CreatePlaceholder(account, item, Translator.RemoteFolders_Loading, isOnlineArchive: true));
        return item;
    }

    /// <summary>A child node for a fetched remote folder; it gets a placeholder when it has children of its own.</summary>
    public static RemoteFolderMenuItem CreateNode(MailAccount account, PublicFolderNode node, RemoteFolderMenuItem parent)
    {
        var isOnlineArchive = parent.IsOnlineArchive;
        var folder = new MailItemFolder
        {
            Id = DeterministicId(account.Id, (isOnlineArchive ? "archive:" : "public:") + node.Id),
            MailAccountId = account.Id,
            RemoteFolderId = node.Id,
            ParentRemoteFolderId = node.ParentId,
            FolderName = string.IsNullOrWhiteSpace(node.Name) ? Translator.RemoteFolders_Unnamed : node.Name,
            SpecialFolderType = isOnlineArchive ? SpecialFolderType.OnlineArchive : SpecialFolderType.PublicFolders,
            IsPublicFolderNode = !isOnlineArchive,
            IsOnlineArchiveNode = isOnlineArchive,
            PublicFolderKind = node.Kind,
        };

        var item = new RemoteFolderMenuItem(folder, account, parent);

        if (node.HasChildren)
        {
            item.SubMenuItems.Add(CreatePlaceholder(account, item, Translator.RemoteFolders_Loading, isOnlineArchive));
        }
        else
        {
            item.MarkChildrenLoaded();
        }

        return item;
    }

    /// <summary>A pinned public mail folder surfaced under the account's folders for quick access.</summary>
    public static RemoteFolderMenuItem CreatePinnedMailFolder(MailAccount account, PublicFolderFavorite favorite, IMenuItem parent)
    {
        var folder = new MailItemFolder
        {
            Id = DeterministicId(account.Id, "pinned:" + favorite.FolderId),
            MailAccountId = account.Id,
            RemoteFolderId = favorite.FolderId,
            FolderName = favorite.DisplayName,
            SpecialFolderType = SpecialFolderType.PublicFolders,
            IsPublicFolderNode = true,
            PublicFolderKind = PublicFolderKind.Mail,
        };

        var item = new RemoteFolderMenuItem(folder, account, parent) { IsPinnedEntry = true, IsPinned = true };
        item.MarkChildrenLoaded();
        return item;
    }

    /// <summary>A non-interactive informational row, for example "Online Archive is not enabled for this account".</summary>
    public static RemoteFolderMenuItem CreateMessageNode(MailAccount account, RemoteFolderMenuItem parent, string message)
        => CreatePlaceholder(account, parent, message, parent.IsOnlineArchive);

    public static bool IsPlaceholder(IMenuItem item) => item is RemoteFolderMenuItem { IsPlaceholder: true };

    private static RemoteFolderMenuItem CreatePlaceholder(MailAccount account, IMenuItem parent, string text, bool isOnlineArchive)
    {
        var folder = new MailItemFolder
        {
            Id = Guid.NewGuid(),
            MailAccountId = account.Id,
            FolderName = text,
            SpecialFolderType = isOnlineArchive ? SpecialFolderType.OnlineArchive : SpecialFolderType.PublicFolders,
            IsPublicFolderNode = !isOnlineArchive,
            IsOnlineArchiveNode = isOnlineArchive,
            IsPublicFolderPlaceholder = true,
            PublicFolderKind = PublicFolderKind.Other,
        };

        var item = new RemoteFolderMenuItem(folder, account, parent);
        item.MarkChildrenLoaded();
        return item;
    }

    /// <summary>Stable per (account, key) so a node keeps the same id across menu rebuilds.</summary>
    public static Guid DeterministicId(Guid accountId, string key)
    {
        var bytes = Encoding.UTF8.GetBytes(accountId.ToString("N") + ":" + key);
        return new Guid(MD5.HashData(bytes));
    }
}
