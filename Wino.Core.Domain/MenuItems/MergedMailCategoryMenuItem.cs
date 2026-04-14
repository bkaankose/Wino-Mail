using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Core.Domain.MenuItems;

public partial class MergedMailCategoryMenuItem : MenuItemBase<List<MailCategory>, IMenuItem>, IMergedAccountFolderMenuItem, IMergedMailCategoryMenuItem
{
    private readonly IReadOnlyList<IMailItemFolder> _handlingFolders;

    [ObservableProperty]
    private int unreadItemCount;

    public MergedMailCategoryMenuItem(List<MailCategory> categories, IEnumerable<IMailItemFolder> handlingFolders, MergedInbox mergedInbox)
        : base(categories, null, null)
    {
        _handlingFolders = handlingFolders?.ToList() ?? [];
        MergedInbox = mergedInbox;
    }

    public string FolderName => Parameter.FirstOrDefault()?.Name ?? string.Empty;
    public bool IsSynchronizationEnabled => false;
    public SpecialFolderType SpecialFolderType => SpecialFolderType.Other;
    public IEnumerable<IMailItemFolder> HandlingFolders => _handlingFolders;
    public bool IsMoveTarget => true;
    public bool IsSticky => false;
    public bool IsSystemFolder => false;
    public bool ShowUnreadCount => true;
    public string AssignedAccountName => MergedInbox?.Name;
    public MergedInbox MergedInbox { get; }
    public string TextColorHex => Parameter.FirstOrDefault()?.TextColorHex;
    public string BackgroundColorHex => Parameter.FirstOrDefault()?.BackgroundColorHex;
    public bool HasTextColor => !string.IsNullOrWhiteSpace(TextColorHex);
    public IReadOnlyList<MailCategory> Categories => Parameter;

    public void UpdateFolder(IMailItemFolder folder)
    {
    }
}
