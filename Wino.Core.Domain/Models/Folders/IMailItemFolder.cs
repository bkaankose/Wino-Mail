using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Folders
{
    public interface IMailItemFolder
    {
        string BackgroundColorHex { get; set; }
        string DeltaToken { get; set; }
        string FolderName { get; set; }
        long HighestModeSeq { get; set; }
        Guid Id { get; set; }
        bool IsHidden { get; set; }
        bool IsSticky { get; set; }
        bool IsSynchronizationEnabled { get; set; }
        bool IsSystemFolder { get; set; }
        DateTime? LastSynchronizedDate { get; set; }
        Guid MailAccountId { get; set; }
        string ParentRemoteFolderId { get; set; }
        string RemoteFolderId { get; set; }
        SpecialFolderType SpecialFolderType { get; set; }
        string TextColorHex { get; set; }
        uint UidValidity { get; set; }
        List<IMailItemFolder> ChildFolders { get; set; }
        bool IsMoveTarget { get; }
        bool ShowUnreadCount { get; set; }

        bool ContainsSpecialFolderType(SpecialFolderType type);
    }
}
