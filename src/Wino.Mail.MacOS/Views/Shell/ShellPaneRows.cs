using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;
using Wino.Mail.Controls.AppKit.Shell;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.MacOS.Views.Shell;

/// <summary>
/// Row heights for every menu item type the Windows pane templates know (ShellMenuTemplates.xaml).
/// Selection, interaction, visibility and child rules are shared in <see cref="ShellMenuItemRules"/>.
/// </summary>
internal static class ShellPaneRows
{
    /// <summary>Indent of child rows under their parent, the Windows pane child offset.</summary>
    public const double ChildIndent = 12;

    public static double Height(IMenuItem item, bool compactAccounts) => item switch
    {
        NewAddressListMenuItem => 44,
        NewMailMenuItem or NewContactMenuItem or NewTaskListMenuItem => 40,
        MergedAccountMenuItem => 44,
        IAccountNavigationMenuItem account when ShellMenuItemRules.IsAccountRow(account) => compactAccounts ? 36 : 44,
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
}
