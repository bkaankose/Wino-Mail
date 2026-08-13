using System.Collections.Generic;
using System.Collections.ObjectModel;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Core.Domain.Interfaces;

public interface IFolderMenuItem : IBaseFolderMenuItem
{
    MailAccount ParentAccount { get; }
    void UpdateParentAccounnt(MailAccount account);
}

public interface IMergedAccountFolderMenuItem : IBaseFolderMenuItem { }

public interface IMailCategoryMenuItem : IBaseFolderMenuItem
{
    Entities.Mail.MailCategory MailCategory { get; }
    string TextColorHex { get; }
    string BackgroundColorHex { get; }
    bool HasTextColor { get; }
}

public interface IMergedMailCategoryMenuItem : IBaseFolderMenuItem
{
    IReadOnlyList<Entities.Mail.MailCategory> Categories { get; }
    string TextColorHex { get; }
    string BackgroundColorHex { get; }
    bool HasTextColor { get; }
}

public interface IBaseFolderMenuItem : IMenuItem
{
    string FolderName { get; }
    bool IsSynchronizationEnabled { get; }
    int UnreadItemCount { get; set; }
    SpecialFolderType SpecialFolderType { get; }
    IEnumerable<IMailItemFolder> HandlingFolders { get; }
    ObservableCollection<IMenuItem> SubMenuItems { get; }
    bool IsMoveTarget { get; }
    bool IsSticky { get; }
    bool IsSystemFolder { get; }
    bool ShowUnreadCount { get; }
    string AssignedAccountName { get; }

    void UpdateFolder(IMailItemFolder folder);
}
