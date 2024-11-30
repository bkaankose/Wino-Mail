using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Menus;

namespace Wino.Services
{
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
        public virtual IEnumerable<MailOperationMenuItem> GetMailItemContextMenuActions(IEnumerable<IMailItem> selectedMailItems)
        {
            if (selectedMailItems == null)
                return default;

            var operationList = new List<MailOperationMenuItem>();

            // Disable archive button for Archive folder itself.

            bool isArchiveFolder = selectedMailItems.All(a => a.AssignedFolder.SpecialFolderType == SpecialFolderType.Archive);
            bool isDraftOrSent = selectedMailItems.All(a => a.AssignedFolder.SpecialFolderType == SpecialFolderType.Draft || a.AssignedFolder.SpecialFolderType == SpecialFolderType.Sent);
            bool isJunkFolder = selectedMailItems.All(a => a.AssignedFolder.SpecialFolderType == SpecialFolderType.Junk);

            bool isSingleItem = selectedMailItems.Count() == 1;

            IMailItem singleItem = selectedMailItems.FirstOrDefault();

            // Archive button.

            if (isArchiveFolder)
                operationList.Add(MailOperationMenuItem.Create(MailOperation.UnArchive));
            else
                operationList.Add(MailOperationMenuItem.Create(MailOperation.Archive));

            // Delete button.
            operationList.Add(MailOperationMenuItem.Create(MailOperation.SoftDelete));

            // Move button.
            operationList.Add(MailOperationMenuItem.Create(MailOperation.Move, !isDraftOrSent));

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
                bool isAllRead = selectedMailItems.All(a => a.IsRead);
                bool isAllUnread = selectedMailItems.All(a => !a.IsRead);
                bool isAllFlagged = selectedMailItems.All(a => a.IsFlagged);
                bool isAllNotFlagged = selectedMailItems.All(a => !a.IsFlagged);

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
            if (isJunkFolder)
                operationList.Add(MailOperationMenuItem.Create(MailOperation.MarkAsNotJunk));
            else if (!isDraftOrSent)
                operationList.Add(MailOperationMenuItem.Create(MailOperation.MoveToJunk));

            // TODO: Focus folder support.

            // Remove the separator if it's the last item remaining.
            // It's creating unpleasent UI glitch.

            if (operationList.LastOrDefault()?.Operation == MailOperation.Seperator)
                operationList.RemoveAt(operationList.Count - 1);

            return operationList;
        }
        public virtual IEnumerable<MailOperationMenuItem> GetMailItemRenderMenuActions(IMailItem mailItem, bool isDarkEditor)
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

            return actionList;
        }
    }
}
