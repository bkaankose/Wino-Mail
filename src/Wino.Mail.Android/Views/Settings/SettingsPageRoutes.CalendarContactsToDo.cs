using Wino.Calendar.ViewModels;
using Wino.Core.Domain.Enums;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Android.Views.Settings.CalendarContactsToDo;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Android.Views.Settings;

/// <summary>Calendar, People and To Do preferences, and one calendar's settings (iOS SettingsPageRoutes, same pages).</summary>
public static partial class SettingsPageRoutes
{
    static partial void RegisterCalendarContactsToDoPages(AndroidPageRegistry registry)
    {
        const AndroidPageHost host = AndroidPageHost.Settings;

        // The iOS InitializeAsync: OnNavigatedTo, then the option load the page binds to.
        registry.Register<CalendarPreferenceSettingsPageFragment, CalendarPreferenceSettingsPageViewModel>(WinoPage.CalendarPreferenceSettingsPage, host,
            CalendarPreferenceSettingsPageFragment.InitializeAsync);
        registry.Register<CalendarPreferenceSettingsPageFragment, CalendarPreferenceSettingsPageViewModel>(WinoPage.CalendarSettingsPage, host,
            CalendarPreferenceSettingsPageFragment.InitializeAsync);
        registry.Register<CalendarRenderingSettingsPageFragment, CalendarRenderingSettingsPageViewModel>(WinoPage.CalendarRenderingSettingsPage, host);
        registry.Register<ContactsPreferenceSettingsPageFragment, ContactsPreferenceSettingsPageViewModel>(WinoPage.ContactsPreferenceSettingsPage, host,
            static (vm, mode, parameter) => vm.InitializeNavigationAsync(mode, parameter));
        registry.Register<ToDoPreferenceSettingsPageFragment, ToDoPreferenceSettingsPageViewModel>(WinoPage.ToDoPreferenceSettingsPage, host,
            static (vm, mode, parameter) => vm.InitializeNavigationAsync(mode, parameter));

        // Reached from account details and the calendar list with an AccountCalendar or an account id.
        registry.Register<CalendarAccountSettingsPageFragment, CalendarAccountSettingsPageViewModel>(WinoPage.CalendarAccountSettingsPage, host,
            CalendarAccountSettingsPageFragment.InitializeAsync);
    }
}
