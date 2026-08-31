using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Menus;

namespace Wino.Services;

public class ContextMenuItemService : IContextMenuItemService
{
    public virtual IEnumerable<FolderOperationMenuItem> GetFolderContextMenuActions(IBaseFolderMenuItem folderInformation)
    {
        var list = new List<FolderOperationMenuItem>();

        if (folderInformation.IsSticky)
            list.Add(FolderOperationMenuItem.Create(FolderOperation.Unpin));
        else
            list.Add(FolderOperationMenuItem.Create(FolderOperation.Pin));

        list.Add(FolderOperationMenuItem.Create(FolderOperation.Seperator));

        // Following 4 items are disabled for system folders.

        list.Add(FolderOperationMenuItem.Create(FolderOperation.Rename, !folderInformation.IsSystemFolder));
        list.Add(FolderOperationMenuItem.Create(FolderOperation.Delete, !folderInformation.IsSystemFolder));
        list.Add(FolderOperationMenuItem.Create(FolderOperation.CreateSubFolder, !folderInformation.IsSystemFolder));

        list.Add(FolderOperationMenuItem.Create(FolderOperation.Seperator));

        list.Add(FolderOperationMenuItem.Create(FolderOperation.Empty));

        list.Add(FolderOperationMenuItem.Create(FolderOperation.MarkAllAsRead));

        return list;
    }
    public virtual IEnumerable<MailOperationMenuItem> GetMailItemContextMenuActions(IEnumerable<MailCopy> selectedMailItems)
    {
        var selectedItems = selectedMailItems?
            .Where(static item => item != null)
            .ToList() ?? [];

        if (selectedItems.Count == 0)
            return [];

        var operationList = new List<MailOperationMenuItem>();

        // Disable archive button for Archive folder itself.

        bool isArchiveFolder = selectedItems.All(a => a.AssignedFolder.SpecialFolderType == SpecialFolderType.Archive);
        bool isDraftOrSent = selectedItems.All(a => a.AssignedFolder.SpecialFolderType == SpecialFolderType.Draft || a.AssignedFolder.SpecialFolderType == SpecialFolderType.Sent);
        bool hasDraftOrSent = selectedItems.Any(a => a.AssignedFolder.SpecialFolderType == SpecialFolderType.Draft || a.AssignedFolder.SpecialFolderType == SpecialFolderType.Sent);
        bool isJunkFolder = selectedItems.All(a => a.AssignedFolder.SpecialFolderType == SpecialFolderType.Junk);
        bool isPop3 = selectedItems.All(a => a.AssignedAccount?.ProviderType == MailProviderType.POP3);

        bool isSingleItem = selectedItems.Count == 1;

        MailCopy singleItem = selectedItems[0];

        if (isSingleItem && singleItem.IsLocalDraft)
        {
            operationList.Add(MailOperationMenuItem.Create(MailOperation.RetryDraftUpload));
            operationList.Add(MailOperationMenuItem.Create(MailOperation.Seperator));
        }

        operationList.Add(MailOperationMenuItem.Create(MailOperation.Reply));
        operationList.Add(MailOperationMenuItem.Create(MailOperation.ReplyAll));
        operationList.Add(MailOperationMenuItem.Create(MailOperation.Forward));
        operationList.Add(MailOperationMenuItem.Create(MailOperation.Seperator));

        // Archive button.

        if (isArchiveFolder)
            operationList.Add(MailOperationMenuItem.Create(MailOperation.UnArchive));
        else
            operationList.Add(MailOperationMenuItem.Create(MailOperation.Archive));

        // Delete button.
        operationList.Add(MailOperationMenuItem.Create(MailOperation.SoftDelete));

        // Move button.
        operationList.Add(MailOperationMenuItem.Create(MailOperation.Move, !hasDraftOrSent));

        // Independent flag, read etc.
        if (isSingleItem)
        {
            if (singleItem.IsFlagged)
                operationList.Add(MailOperationMenuItem.Create(MailOperation.ClearFlag));
            else
                operationList.Add(MailOperationMenuItem.Create(MailOperation.SetFlag));

            if (singleItem.IsRead)
                operationList.Add(MailOperationMenuItem.Create(MailOperation.MarkAsUnread));
            else
                operationList.Add(MailOperationMenuItem.Create(MailOperation.MarkAsRead));
        }
        else
        {
            bool isAllRead = selectedItems.All(a => a.IsRead);
            bool isAllUnread = selectedItems.All(a => !a.IsRead);
            bool isAllFlagged = selectedItems.All(a => a.IsFlagged);
            bool isAllNotFlagged = selectedItems.All(a => !a.IsFlagged);

            List<MailOperationMenuItem> readOperations = (isAllRead, isAllUnread) switch
            {
                (true, false) => [MailOperationMenuItem.Create(MailOperation.MarkAsUnread)],
                (false, true) => [MailOperationMenuItem.Create(MailOperation.MarkAsRead)],
                _ => [MailOperationMenuItem.Create(MailOperation.MarkAsRead), MailOperationMenuItem.Create(MailOperation.MarkAsUnread)]
            };
            operationList.AddRange(readOperations);

            List<MailOperationMenuItem> flagsOperations = (isAllFlagged, isAllNotFlagged) switch
            {
                (true, false) => [MailOperationMenuItem.Create(MailOperation.ClearFlag)],
                (false, true) => [MailOperationMenuItem.Create(MailOperation.SetFlag)],
                _ => [MailOperationMenuItem.Create(MailOperation.SetFlag), MailOperationMenuItem.Create(MailOperation.ClearFlag)]
            };
            operationList.AddRange(flagsOperations);
        }

        // Ignore
        if (!isDraftOrSent)
            operationList.Add(MailOperationMenuItem.Create(MailOperation.Ignore));

        // Seperator
        operationList.Add(MailOperationMenuItem.Create(MailOperation.Seperator));

        // Junk folder
        if (isJunkFolder && !isPop3)
            operationList.Add(MailOperationMenuItem.Create(MailOperation.MarkAsNotJunk));
        else if (!isDraftOrSent && !isPop3)
            operationList.Add(MailOperationMenuItem.Create(MailOperation.MoveToJunk));

        AddFocusedInboxActions(operationList, selectedItems);

        // Remove the separator if it's the last item remaining.
        // It's creating unpleasent UI glitch.

        if (operationList.LastOrDefault()?.Operation == MailOperation.Seperator)
            operationList.RemoveAt(operationList.Count - 1);

        return operationList;
    }
    public virtual IEnumerable<MailOperationMenuItem> GetMailItemRenderMenuActions(MailCopy mailItem, bool isDarkEditor)
    {
        var actionList = new List<MailOperationMenuItem>();

        bool isArchiveFolder = mailItem.AssignedFolder.SpecialFolderType == SpecialFolderType.Archive;

        // Add light/dark editor theme switch.
        if (isDarkEditor)
            actionList.Add(MailOperationMenuItem.Create(MailOperation.LightEditor));
        else
            actionList.Add(MailOperationMenuItem.Create(MailOperation.DarkEditor));

        actionList.Add(MailOperationMenuItem.Create(MailOperation.Seperator));

        // You can't do these to draft items.
        if (!mailItem.IsDraft)
        {
            // Reply
            actionList.Add(MailOperationMenuItem.Create(MailOperation.Reply));

            // Reply All
            actionList.Add(MailOperationMenuItem.Create(MailOperation.ReplyAll));

            // Forward
            actionList.Add(MailOperationMenuItem.Create(MailOperation.Forward));
        }

        // Archive - Unarchive
        if (isArchiveFolder)
            actionList.Add(MailOperationMenuItem.Create(MailOperation.UnArchive));
        else
            actionList.Add(MailOperationMenuItem.Create(MailOperation.Archive));

        // Delete
        actionList.Add(MailOperationMenuItem.Create(MailOperation.SoftDelete));

        // Flag - Clear Flag
        if (mailItem.IsFlagged)
            actionList.Add(MailOperationMenuItem.Create(MailOperation.ClearFlag));
        else
            actionList.Add(MailOperationMenuItem.Create(MailOperation.SetFlag));

        // Secondary items.

        // Read - Unread
        if (mailItem.IsRead)
            actionList.Add(MailOperationMenuItem.Create(MailOperation.MarkAsUnread, true, false));
        else
            actionList.Add(MailOperationMenuItem.Create(MailOperation.MarkAsRead, true, false));

        if (mailItem.AssignedFolder.SpecialFolderType == SpecialFolderType.Junk && mailItem.AssignedAccount?.ProviderType != MailProviderType.POP3)
            actionList.Add(MailOperationMenuItem.Create(MailOperation.MarkAsNotJunk, true, true));
        else if (!mailItem.IsDraft && mailItem.AssignedFolder.SpecialFolderType != SpecialFolderType.Sent && mailItem.AssignedAccount?.ProviderType != MailProviderType.POP3)
            actionList.Add(MailOperationMenuItem.Create(MailOperation.MoveToJunk, true, true));

        if (IsOutlookInboxMail(mailItem))
        {
            var moveOperation = mailItem.IsFocused ? MailOperation.MoveToOther : MailOperation.MoveToFocused;
            var alwaysMoveOperation = mailItem.IsFocused ? MailOperation.AlwaysMoveToOther : MailOperation.AlwaysMoveToFocused;

            actionList.Add(MailOperationMenuItem.Create(moveOperation, true, true));
            actionList.Add(MailOperationMenuItem.Create(alwaysMoveOperation, true, true));
        }

        return actionList;
    }

    private static void AddFocusedInboxActions(List<MailOperationMenuItem> operationList, IReadOnlyCollection<MailCopy> selectedItems)
    {
        if (selectedItems.Count == 0 || !selectedItems.All(IsOutlookInboxMail))
            return;

        operationList.Add(MailOperationMenuItem.Create(MailOperation.Seperator));

        var allFocused = selectedItems.All(static item => item.IsFocused);
        var allOther = selectedItems.All(static item => !item.IsFocused);

        if (!allFocused)
            operationList.Add(MailOperationMenuItem.Create(MailOperation.MoveToFocused));

        if (!allOther)
            operationList.Add(MailOperationMenuItem.Create(MailOperation.MoveToOther));

        // An override is sender-specific, so expose the "Always" action only for one message.
        if (selectedItems.Count == 1)
        {
            operationList.Add(MailOperationMenuItem.Create(
                allFocused ? MailOperation.AlwaysMoveToOther : MailOperation.AlwaysMoveToFocused));
        }
    }

    private static bool IsOutlookInboxMail(MailCopy mailItem)
        => mailItem?.AssignedAccount?.ProviderType == MailProviderType.Outlook
           && mailItem.AssignedFolder?.SpecialFolderType == SpecialFolderType.Inbox
           && !mailItem.IsDraft;
}
