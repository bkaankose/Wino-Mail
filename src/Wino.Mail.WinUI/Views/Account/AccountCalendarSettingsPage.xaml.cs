using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Calendar;
using Wino.Mail.ViewModels.Common.Data;
using Wino.Mail.WinUI.Views.Abstract;

namespace Wino.Mail.WinUI.Views.Account;

public sealed partial class AccountCalendarSettingsPage : AccountCalendarSettingsPageAbstract
{
    public AccountCalendarSettingsPage()
    {
        InitializeComponent();
    }

    private async void CalendarSynchronizationToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { Tag: AccountCalendarSettingsItemViewModel calendarItem } toggleSwitch)
            await ViewModel.UpdateCalendarSynchronizationAsync(calendarItem, toggleSwitch.IsOn);
    }

    private async void CalendarShowAsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { Tag: AccountCalendarSettingsItemViewModel calendarItem, SelectedItem: AccountCalendarShowAsOption option })
            await ViewModel.UpdateCalendarDefaultShowAsAsync(calendarItem, option);
    }

    private async void CalendarDefaultReminderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { Tag: AccountCalendarSettingsItemViewModel calendarItem, SelectedItem: CalendarDefaultReminderOption option })
            await ViewModel.UpdateCalendarDefaultReminderAsync(calendarItem, option);
    }

    private async void CalendarColorItemClick(object sender, ItemClickEventArgs e)
    {
        if (sender is GridView { Tag: AccountCalendarSettingsItemViewModel calendarItem } && e.ClickedItem is AppColorViewModel color)
            await ViewModel.UpdateCalendarColorAsync(calendarItem, color);
    }
}
