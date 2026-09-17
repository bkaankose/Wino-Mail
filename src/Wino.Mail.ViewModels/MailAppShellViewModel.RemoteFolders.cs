using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Core.Domain.Models.PublicFolders;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels;

/// <summary>
/// The read-only remote trees of an Exchange account (public folders and the online archive). The nodes
/// come from <see cref="IFolderService"/> with a placeholder child; the first expansion replaces it with
/// the folder's real children, fetched live, and the context menu pins or unpins public mail folders.
/// </summary>
public partial class MailAppShellViewModel
{
    private readonly IPublicFolderService _publicFolderService;
    private readonly IOnlineArchiveService _onlineArchiveService;
    private readonly IPublicFolderFavoriteService _publicFolderFavoriteService;

    private readonly HashSet<Guid> _loadingRemoteFolderIds = new();
    private readonly HashSet<Guid> _expandedRemoteFolderIds = new();

    /// <summary>Subscribes every remote node in a freshly built folder list (the roots and pinned entries).</summary>
    private void AttachRemoteFolderHandlers(IEnumerable<IMenuItem> folders)
    {
        if (folders == null)
            return;

        foreach (var item in folders)
        {
            if (item is RemoteFolderMenuItem remoteFolder)
            {
                AttachRemoteFolderHandlers(remoteFolder);
            }
            else if (item is IBaseFolderMenuItem folder)
            {
                AttachRemoteFolderHandlers(folder.SubMenuItems);
            }
        }
    }

    private void AttachRemoteFolderHandlers(RemoteFolderMenuItem node)
    {
        node.ChildrenRequested -= RemoteFolderChildrenRequested;
        node.ChildrenRequested += RemoteFolderChildrenRequested;
        node.PinToggleRequested -= RemoteFolderPinToggleRequested;
        node.PinToggleRequested += RemoteFolderPinToggleRequested;

        if (node.CanPin && _publicFolderFavoriteService != null)
        {
            node.IsPinned = _publicFolderFavoriteService.IsFavorite(node.ParentAccount.Id, node.RemoteFolder.RemoteFolderId);
        }

        RestoreRemoteFolderExpansion(node);
    }

    /// <summary>
    /// Children load on the change to expanded, and a rebuilt folder list (returning to Mail from another
    /// mode, a pin, a folder sync) never produces that change for a node the user had open: the nodes are
    /// new and collapsed while the navigation view keeps drawing them open, so they would sit on their
    /// "Loading" row until collapsed and expanded again. The ids of the open nodes are therefore kept
    /// here (they are deterministic), and a rebuilt node found in the set is expanded and loaded at once.
    /// </summary>
    private void RestoreRemoteFolderExpansion(RemoteFolderMenuItem node)
    {
        if (node.IsPlaceholder)
            return;

        node.PropertyChanged -= RemoteFolderPropertyChanged;
        node.PropertyChanged += RemoteFolderPropertyChanged;

        bool wasExpanded;
        lock (_expandedRemoteFolderIds)
        {
            wasExpanded = _expandedRemoteFolderIds.Contains(node.RemoteFolder.Id);
        }

        if (node.AreChildrenLoaded || !(wasExpanded || node.IsExpanded))
            return;

        Log.Debug("Restoring the expanded remote folder {FolderName}.", node.RemoteFolder.FolderName);

        if (node.IsExpanded)
        {
            _ = LoadRemoteFolderChildrenAsync(node);
        }
        else
        {
            // Raises ChildrenRequested, which is subscribed by now.
            node.IsExpanded = true;
        }
    }

    private void RemoteFolderPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RemoteFolderMenuItem.IsExpanded) || sender is not RemoteFolderMenuItem node)
            return;

        if (node.IsExpanded)
        {
            lock (_expandedRemoteFolderIds)
            {
                _expandedRemoteFolderIds.Add(node.RemoteFolder.Id);
            }

            return;
        }

        _ = ForgetCollapsedRemoteFolderAsync(node);
    }

    /// <summary>
    /// Only a collapse by the user forgets a node. Clearing the folder area also collapses every item just
    /// before removing it, so the decision waits until that has played out: a node that is no longer in
    /// the menu was torn down, not collapsed.
    /// </summary>
    private async Task ForgetCollapsedRemoteFolderAsync(RemoteFolderMenuItem node)
    {
        await Task.Delay(300).ConfigureAwait(false);

        await ExecuteUIThread(() =>
        {
            if (node.IsExpanded || !IsInMenu(MenuItems, node))
                return;

            lock (_expandedRemoteFolderIds)
            {
                _expandedRemoteFolderIds.Remove(node.RemoteFolder.Id);
            }
        });
    }

    private static bool IsInMenu(IEnumerable<IMenuItem> items, IMenuItem target)
    {
        if (items == null)
            return false;

        foreach (var item in items.ToList())
        {
            if (ReferenceEquals(item, target))
                return true;

            IEnumerable<IMenuItem> children = item switch
            {
                IBaseFolderMenuItem folder => folder.SubMenuItems,
                AccountMenuItem account => account.SubMenuItems,
                _ => null
            };

            if (IsInMenu(children, target))
                return true;
        }

        return false;
    }

    private async void RemoteFolderChildrenRequested(object sender, EventArgs e)
    {
        if (sender is RemoteFolderMenuItem node)
        {
            await LoadRemoteFolderChildrenAsync(node);
        }
    }

    /// <summary>
    /// Replaces a remote node's placeholder with its real children. A null archive root means the mailbox
    /// has no archive provisioned, so an informational row is shown instead. The new rows are added before
    /// the placeholder is removed so the expanded node is never momentarily empty.
    /// </summary>
    private async Task LoadRemoteFolderChildrenAsync(RemoteFolderMenuItem node)
    {
        var folder = node.RemoteFolder;

        if (node.AreChildrenLoaded || !_loadingRemoteFolderIds.Add(folder.Id))
            return;

        try
        {
            var accountId = node.ParentAccount.Id;
            IReadOnlyList<PublicFolderNode> children;

            if (node.IsOnlineArchive)
            {
                if (_onlineArchiveService == null)
                    return;

                children = string.IsNullOrEmpty(folder.RemoteFolderId)
                    ? await _onlineArchiveService.GetRootFoldersAsync(accountId).ConfigureAwait(false)
                    : await _onlineArchiveService.GetChildrenAsync(accountId, folder.RemoteFolderId).ConfigureAwait(false);
            }
            else
            {
                if (_publicFolderService == null)
                    return;

                children = string.IsNullOrEmpty(folder.RemoteFolderId)
                    ? await _publicFolderService.GetRootChildrenAsync(accountId).ConfigureAwait(false)
                    : await _publicFolderService.GetChildrenAsync(accountId, folder.RemoteFolderId).ConfigureAwait(false);
            }

            await ExecuteUIThread(() =>
            {
                var loadingRows = node.SubMenuItems.Where(PublicFolderMenuItemFactory.IsPlaceholder).ToList();

                if (children == null)
                {
                    node.SubMenuItems.Add(PublicFolderMenuItemFactory.CreateMessageNode(node.ParentAccount, node, Translator.OnlineArchive_NotEnabled));
                }
                else
                {
                    foreach (var child in children)
                    {
                        var childItem = PublicFolderMenuItemFactory.CreateNode(node.ParentAccount, child, node);
                        AttachRemoteFolderHandlers(childItem);
                        node.SubMenuItems.Add(childItem);
                    }
                }

                foreach (var loadingRow in loadingRows)
                {
                    node.SubMenuItems.Remove(loadingRow);
                }

                node.MarkChildrenLoaded();
            });
        }
        catch (Exception ex)
        {
            // Keep the placeholder and the unloaded state so the user can retry by expanding again.
            Log.Warning(ex, "Failed to load remote folder children for account {AccountId}.", folder.MailAccountId);

            await ExecuteUIThread(() =>
                _dialogService.InfoBarMessage(Translator.GeneralTitle_Error, string.Format(Translator.RemoteFolders_LoadFailed, ex.Message), InfoBarMessageType.Error));
        }
        finally
        {
            _loadingRemoteFolderIds.Remove(folder.Id);
        }
    }

    private async void RemoteFolderPinToggleRequested(object sender, EventArgs e)
    {
        if (sender is not RemoteFolderMenuItem node || !node.CanPin || _publicFolderFavoriteService == null)
            return;

        var accountId = node.ParentAccount.Id;
        var folderId = node.RemoteFolder.RemoteFolderId;
        var isPinning = !_publicFolderFavoriteService.IsFavorite(accountId, folderId);

        if (isPinning)
        {
            _publicFolderFavoriteService.AddFavorite(PublicFolderFavorite.Create(accountId, folderId, node.Kind, node.RemoteFolder.FolderName));
        }
        else
        {
            _publicFolderFavoriteService.RemoveFavorite(accountId, folderId);
        }

        // A mail pin lives in this very folder list, which is rebuilt to show or drop it. Contact and
        // calendar pins surface in People and Calendar, so the tree stays as the user left it.
        if (node.Kind == PublicFolderKind.Mail)
        {
            await RefreshLoadedAccountFolderStructureAsync(accountId);
            return;
        }

        await ExecuteUIThread(() =>
        {
            node.IsPinned = isPinning;

            if (!isPinning)
                return;

            _dialogService.InfoBarMessage(
                Translator.GeneralTitle_Info,
                string.Format(
                    node.Kind == PublicFolderKind.Contacts ? Translator.PublicFolders_PinnedToPeople : Translator.PublicFolders_PinnedToCalendar,
                    node.RemoteFolder.FolderName),
                InfoBarMessageType.Success);
        });
    }

    /// <summary>
    /// A contact or calendar folder can also be unpinned from People or Calendar; the loaded tree nodes
    /// then have to offer "pin" again.
    /// </summary>
    public void Receive(PublicFolderFavoritesChanged message)
    {
        if (message.Kind == PublicFolderKind.Mail || _publicFolderFavoriteService == null)
            return;

        _ = ExecuteUIThread(() => RefreshRemoteFolderPinStates(MenuItems, message.AccountId));
    }

    private void RefreshRemoteFolderPinStates(IEnumerable<IMenuItem> items, Guid accountId)
    {
        if (items == null)
            return;

        foreach (var item in items)
        {
            if (item is RemoteFolderMenuItem { CanPin: true, IsPinnedEntry: false } node && node.ParentAccount?.Id == accountId)
            {
                node.IsPinned = _publicFolderFavoriteService.IsFavorite(accountId, node.RemoteFolder.RemoteFolderId);
            }

            if (item is IBaseFolderMenuItem folder)
            {
                RefreshRemoteFolderPinStates(folder.SubMenuItems, accountId);
            }
            else if (item is AccountMenuItem account)
            {
                RefreshRemoteFolderPinStates(account.SubMenuItems, accountId);
            }
        }
    }
}
