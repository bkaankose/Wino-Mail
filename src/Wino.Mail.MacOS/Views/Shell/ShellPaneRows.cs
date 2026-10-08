using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Mail.Controls.AppKit.Shell;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.MacOS.Views.Shell;

/// <summary>
/// Row metrics and behaviour for every menu item type the Windows pane templates know
/// (ShellMenuTemplates.xaml): heights, whether a click selects or only invokes, which rows
/// highlight, which are hidden while disabled, and where child items come from.
/// </summary>
internal static class ShellPaneRows
{
    /// <summary>Indent of child rows under their parent, the Windows pane child offset.</summary>
    public const double ChildIndent = 12;

    public static double Height(IMenuItem item) => item switch
    {
        NewAddressListMenuItem => 44,
        NewMailMenuItem or NewContactMenuItem or NewTaskListMenuItem => 50,
        MergedAccountMenuItem => 50,
        IAccountNavigationMenuItem account when IsAccountRow(account) => 50,
        FixAccountIssuesMenuItem => 58,
        ShellSectionHeaderMenuItem => 28,
        SeperatorItem => 9,
        CalendarDatePickerMenuItem picker => WinoMiniCalendarView.HeightFor(!picker.IsCalendarExpanded) + 10,
        AccountCalendarGroupMenuItem group => CalendarGroupHeight(group),
        UngroupedCalendarMenuItem => 40,
        TaskSmartViewMenuItem or AccountTaskListMenuItem or AccountTaskListGroupMenuItem => 38,
        MergedAccountMoreFolderMenuItem => 32,
        _ => 36
    };

    public static double CalendarGroupHeight(AccountCalendarGroupMenuItem group)
        => ShellCalendarGroupCell.HeaderHeight + (group.Parameter.IsExpanded ? group.Parameter.AccountCalendars.Count * ShellCalendarGroupCell.CalendarRowHeight : 0) + 4;

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

    public static bool IsEnabled(IMenuItem item)
        => item is not MenuItemBase { IsEnabled: false } && item is not IAccountNavigationMenuItem { IsNavigationEnabled: false };

    /// <summary>Windows collapses these entries while they are disabled instead of greying them out.</summary>
    public static bool IsShown(IMenuItem item)
        => item is not (NewContactMenuItem or NewAddressListMenuItem) || ((MenuItemBase)item).IsEnabled;

    /// <summary>Whether the model item carries the accent indicator while it is not the pane selection.</summary>
    public static bool ShowsIndicator(IMenuItem item)
        => item is IAccountNavigationMenuItem { IsSelected: true } account && IsAccountRow(account);

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

    /// <summary>Same map as Windows XamlHelpers.GetSpecialFolderPathIconGeometry.</summary>
    public static WinoIconGlyph FolderGlyph(SpecialFolderType type) => type switch
    {
        SpecialFolderType.Inbox => WinoIconGlyph.SpecialFolderInbox,
        SpecialFolderType.Starred => WinoIconGlyph.SpecialFolderStarred,
        SpecialFolderType.Important => WinoIconGlyph.SpecialFolderImportant,
        SpecialFolderType.Sent => WinoIconGlyph.SpecialFolderSent,
        SpecialFolderType.Draft => WinoIconGlyph.SpecialFolderDraft,
        SpecialFolderType.Archive => WinoIconGlyph.SpecialFolderArchive,
        SpecialFolderType.Deleted => WinoIconGlyph.SpecialFolderDeleted,
        SpecialFolderType.Junk => WinoIconGlyph.SpecialFolderJunk,
        SpecialFolderType.Chat => WinoIconGlyph.SpecialFolderChat,
        SpecialFolderType.Category => WinoIconGlyph.SpecialFolderCategory,
        SpecialFolderType.Unread => WinoIconGlyph.SpecialFolderUnread,
        SpecialFolderType.Forums => WinoIconGlyph.SpecialFolderForums,
        SpecialFolderType.Updates => WinoIconGlyph.SpecialFolderUpdated,
        SpecialFolderType.Personal => WinoIconGlyph.SpecialFolderPersonal,
        SpecialFolderType.Promotions => WinoIconGlyph.SpecialFolderPromotions,
        SpecialFolderType.Social => WinoIconGlyph.SpecialFolderSocial,
        SpecialFolderType.Other => WinoIconGlyph.SpecialFolderOther,
        SpecialFolderType.More => WinoIconGlyph.SpecialFolderMore,
        _ => WinoIconGlyph.None
    };
}
