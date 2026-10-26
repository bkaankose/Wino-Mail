using System;
using System.Collections.Generic;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// Behaviour of every shell pane item type, as the Windows pane templates define it
/// (ShellMenuTemplates.xaml): whether a click selects or only invokes, which rows react and
/// highlight, which are hidden while disabled, and where child items come from. Native shells
/// read these rules instead of re-encoding the templates.
/// </summary>
public static class ShellMenuItemRules
{
    /// <summary>Contacts draw their account filters with the shared account row, like Windows.</summary>
    public static bool IsAccountRow(IAccountNavigationMenuItem account) => account is not ContactFilterViewModel filter || filter.HasAccountIcon;

    /// <summary>Mirrors each template's SelectsOnInvoked.</summary>
    public static bool SelectsOnInvoked(IMenuItem item) => item switch
    {
        NewMailMenuItem or NewContactMenuItem or NewAddressListMenuItem or NewTaskListMenuItem => false,
        ShellSectionHeaderMenuItem or SeperatorItem or FixAccountIssuesMenuItem => false,
        CalendarDatePickerMenuItem or AccountCalendarGroupMenuItem or UngroupedCalendarMenuItem => false,
        ContactCategoriesExpanderMenuItem or AccountTaskListGroupMenuItem or RateMenuItem or SettingsItem => false,
        MergedAccountMenuItem => false,
        IAccountNavigationMenuItem account => account.SelectsOnInvoked,
        FolderMenuItem folder => folder.IsMoveTarget,
        _ => true
    };

    /// <summary>Rows that react to a click at all.</summary>
    public static bool IsInteractive(IMenuItem item)
        => item is not (ShellSectionHeaderMenuItem or SeperatorItem or CalendarDatePickerMenuItem or AccountCalendarGroupMenuItem);

    /// <summary>Rows that may show the selection fill.</summary>
    public static bool IsHighlightable(IMenuItem item) => IsInteractive(item) && item is not UngroupedCalendarMenuItem;

    /// <summary>
    /// Account rows follow IsNavigationEnabled only, like the Windows account templates: the shell turns IsEnabled off
    /// while an account loads its folders, and reading it would dim every account row on each switch.
    /// </summary>
    public static bool IsEnabled(IMenuItem item)
        => item switch
        {
            IAccountNavigationMenuItem account => account.IsNavigationEnabled,
            IAccountMenuItem => true,
            _ => item is not MenuItemBase { IsEnabled: false }
        };

    /// <summary>Windows collapses these entries while they are disabled instead of greying them out.</summary>
    public static bool IsShown(IMenuItem item)
        => item is not (NewContactMenuItem or NewAddressListMenuItem) || item is MenuItemBase { IsEnabled: true };

    /// <summary>Whether the model item carries the accent indicator while it is not the pane selection.</summary>
    public static bool ShowsIndicator(IMenuItem item) => item switch
    {
        IAccountNavigationMenuItem { IsSelected: true } account => IsAccountRow(account),
        IAccountMenuItem { IsSelected: true } => true,
        _ => false
    };

    /// <summary>
    /// How deep a folder row sits under other folders (More, a parent folder), counted from its account whether or not
    /// the account row is part of the flattened list: the children of More indent one level.
    /// </summary>
    public static int FolderIndent(IMenuItem item)
    {
        int indent = 0;
        for (var parent = item.ParentMenuItem; parent is not null and not IAccountMenuItem; parent = parent.ParentMenuItem)
            indent++;
        return indent;
    }

    public static IEnumerable<IMenuItem> Children(IMenuItem item) => item switch
    {
        AccountMenuItem account => account.SubMenuItems,
        MergedAccountMenuItem merged => merged.SubMenuItems,
        MergedAccountMoreFolderMenuItem more => more.SubMenuItems,
        IBaseFolderMenuItem folder => folder.SubMenuItems ?? (IEnumerable<IMenuItem>)Array.Empty<IMenuItem>(),
        AccountTaskListAccountMenuItem taskAccount => taskAccount.SubMenuItems,
        AccountTaskListGroupMenuItem group => group.SubMenuItems,
        _ => Array.Empty<IMenuItem>()
    };
}
