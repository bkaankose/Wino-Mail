using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Wino.Calendar.Views;
using Wino.Calendar.Views.Account;
using Wino.Calendar.Views.Settings;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.UWP.Services;
using Wino.Views;

namespace Wino.Calendar.Services
{
    public class NavigationService : NavigationServiceBase, INavigationService
    {
        public Type GetPageType(WinoPage winoPage)
        {
            return winoPage switch
            {
                WinoPage.CalendarPage => typeof(CalendarPage),
                WinoPage.SettingsPage => typeof(SettingsPage),
                WinoPage.CalendarSettingsPage => typeof(CalendarSettingsPage),
                WinoPage.AccountManagementPage => typeof(AccountManagementPage),
                WinoPage.ManageAccountsPage => typeof(ManageAccountsPage),
                WinoPage.PersonalizationPage => typeof(PersonalizationPage),
                WinoPage.AccountDetailsPage => typeof(AccountDetailsPage),
                _ => throw new Exception("Page is not implemented yet."),
            };
        }

        public bool Navigate(WinoPage page, object parameter = null, NavigationReferenceFrame frame = NavigationReferenceFrame.ShellFrame, NavigationTransitionType transition = NavigationTransitionType.None)
        {
            // All navigations are performed on shell frame for calendar.

            if (Window.Current.Content is Frame appFrame && appFrame.Content is AppShell shellPage)
            {
                var shellFrame = shellPage.GetShellFrame();

                var pageType = GetPageType(page);

                shellFrame.Navigate(pageType, parameter);
                return true;
            }

            return false;
        }
    }
}
