using Wino.Calendar.ViewModels;
using Wino.Mail.Uwp;

namespace Wino.Calendar.Views.Abstract;

public abstract class CalendarPageAbstract : BasePage<CalendarPageViewModel>
{
    protected CalendarPageAbstract()
    {
        NavigationCacheMode = Windows.UI.Xaml.Navigation.NavigationCacheMode.Disabled;
    }
}
