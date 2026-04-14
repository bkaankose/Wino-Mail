using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Core.Domain.MenuItems;

public partial class MailCategoryMenuItem : MenuItemBase<MailCategory, IMenuItem>, IFolderMenuItem, IMailCategoryMenuItem
{
    private IReadOnlyList<IMailItemFolder> _handlingFolders;

    [ObservableProperty]
    private int unreadItemCount;

    public MailCategoryMenuItem(MailCategory category, MailAccount parentAccount, IEnumerable<IMailItemFolder> handlingFolders, IMenuItem parentMenuItem)
        : base(category, category.Id, parentMenuItem)
    {
        ParentAccount = parentAccount;
        _handlingFolders = handlingFolders?.ToList() ?? [];
    }

    public string FolderName => Parameter.Name;
    public bool IsSynchronizationEnabled => false;
    public SpecialFolderType SpecialFolderType => SpecialFolderType.Other;
    public IEnumerable<IMailItemFolder> HandlingFolders => _handlingFolders;
    public new ObservableCollection<IMenuItem> SubMenuItems { get; } = [];
    public bool IsMoveTarget => true;
    public bool IsSticky => false;
    public bool IsSystemFolder => false;
    public bool ShowUnreadCount => true;
    public string AssignedAccountName => ParentAccount?.Name;
    public MailAccount ParentAccount { get; private set; }
    public string TextColorHex => Parameter.TextColorHex;
    public string BackgroundColorHex => Parameter.BackgroundColorHex;
    public bool HasTextColor => !string.IsNullOrWhiteSpace(Parameter.TextColorHex);
    public MailCategory MailCategory => Parameter;

    public void UpdateFolder(IMailItemFolder folder)
    {
    }

    public void UpdateParentAccounnt(MailAccount account) => ParentAccount = account;
}
