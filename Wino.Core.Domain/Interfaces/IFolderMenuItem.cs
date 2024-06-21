using System.Collections.Generic;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Core.Domain.Interfaces
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
        bool IsMoveTarget { get; }
        bool IsSticky { get; }
        bool IsSystemFolder { get; }
        bool ShowUnreadCount { get; }
        string AssignedAccountName { get; }

        void UpdateFolder(IMailItemFolder folder);
    }
}
