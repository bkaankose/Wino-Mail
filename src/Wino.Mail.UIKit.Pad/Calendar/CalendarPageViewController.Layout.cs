using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Pad.Shell;

namespace Wino.Mail.UIKit.Pad.Calendar;

/// <summary>
/// The page's part of the pad shell's per-mode policy (<see cref="PadShellViewController"/>, Modes partial): the shell
/// sizes the columns for Calendar; the page re-applies the narrow toolbar rule when its column width crosses
/// <see cref="PadShellViewController.CompactContentWidth"/> and puts <see cref="PadCalendarSidebarViewController"/> in the
/// sidebar's content slot while it is the Calendar content.
/// </summary>
public sealed partial class CalendarPageViewController
{
    private PadCalendarSidebarViewController? _sidebar;
    private PadShellViewController? _sidebarShell;

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        ApplyPeriodItems();
    }

    private void AttachSidebar()
    {
        if (PadShell is not { } shell) return;
        var sidebar = new PadCalendarSidebarViewController(Shell, GoToDate, NewEvent);
        _sidebar = sidebar;
        _sidebarShell = shell;
        if (ViewModel.CurrentVisibleRange is { } range) sidebar.SetRange(range);
        if (ViewModel.CurrentSettings is { } settings) sidebar.SetFirstDayOfWeek(settings.FirstDayOfWeek);
        shell.SetSidebarContent(WinoApplicationMode.Calendar, sidebar);
    }

    private void DetachSidebar()
    {
        var sidebar = _sidebar;
        var shell = _sidebarShell;
        _sidebar = null;
        _sidebarShell = null;
        if (sidebar is null) return;
        shell?.ClearSidebarContent(sidebar);
        sidebar.Dispose();
    }
}
