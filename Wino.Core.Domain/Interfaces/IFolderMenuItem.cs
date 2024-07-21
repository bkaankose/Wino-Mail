using System.Collections.Generic;
using Wino.Domain.Entities;
using Wino.Domain.Enums;
using Wino.Domain.Models.Folders;

namespace Wino.Domain.Interfaces
{
    public interface IFolderMenuItem : IBaseFolderMenuItem
    {
        MailAccount ParentAccount { get; }
        void UpdateParentAccounnt(MailAccount account);
    }

    public interface IMergedAccountFolderMenuItem : IBaseFolderMenuItem { }

    public interface IBaseFolderMenuItem : IMenuItem
    {
        string FolderName { get; }
        bool IsSynchronizationEnabled { get; }
        int UnreadItemCount { get; set; }
        SpecialFolderType SpecialFolderType { get; }
        IEnumerable<IMailItemFolder> HandlingFolders { get; }
        IEnumerable<IMenuItem> SubMenuItems { get; }
        bool IsMoveTarget { get; }
        bool IsSticky { get; }
        bool IsSystemFolder { get; }
        bool ShowUnreadCount { get; }
        string AssignedAccountName { get; }

        void UpdateFolder(IMailItemFolder folder);
    }
}
