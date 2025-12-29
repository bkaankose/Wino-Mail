using Wino.Calendar.ViewModels;
using Wino.Mail.WinUI;

namespace Wino.Calendar.Views.Abstract;

public abstract class CalendarPageAbstract : BasePage<CalendarPageViewModel>
{
    protected CalendarPageAbstract()
    {
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Enabled;
    }
}
