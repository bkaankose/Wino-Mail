using Wino.Mail.Linux.Infrastructure;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>Keyboard movement for the shell: the surface as the content pane, Alt+Down and Alt+Up through periods, Ctrl+T for today, Alt+1 to Alt+4 for the views in toolbar order.</summary>
internal sealed partial class CalendarPageView : IPaneFocus, IItemNavigation, IKeyActionTarget
{
    public bool FocusPrimary()
    {
        if (IsReleased || _surface is not { } surface || !surface.Root.GetMapped()) return false;
        surface.FocusSurface();
        return true;
    }

    public bool MoveItem(int delta)
    {
        if (IsReleased) return false;
        NavigatePeriods(delta);
        return true;
    }

    public bool TryHandleKeyAction(string action)
    {
        if (IsReleased) return false;
        if (action == LinuxKeymap.CalendarToday)
        {
            RequestToday();
            return true;
        }
        if (LinuxKeymap.CalendarViewOf(action) is not { } type) return false;
        ViewModel.StatePersistanceService.CalendarDisplayType = type;
        return true;
    }
}
