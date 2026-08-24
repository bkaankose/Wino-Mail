using Wino.Calendar.ViewModels;
using Wino.Mail.WinUI;

namespace Wino.Calendar.Views.Abstract;

public abstract class CalendarPageAbstract : BasePage<CalendarPageViewModel>
{
    protected CalendarPageAbstract()
    {
        // The calendar is the calendar mode root. Caching it keeps the rendered surface and
        // its visible range alive while the event details page is on top of it.
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
    }
}
