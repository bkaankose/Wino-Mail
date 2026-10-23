using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MailCal.Core.ViewModels.Data;
using MailCal.Core.Domain.Models.Folders;
using MailCal.Mail.ViewModels;
using MailCal.Views.Abstract;
using MailCal.Helpers;
using WinRT;

namespace MailCal.Views;

public sealed partial class AccountDetailsPage : AccountDetailsPageAbstract
{
    public AccountDetailsPage()
    {
        InitializeComponent();
    }

    [DynamicWindowsRuntimeCast(typeof(CheckBox))]
    private async void SyncFolderToggled(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.Tag is IMailItemFolder folder)
        {
            await ViewModel.FolderSyncToggledAsync(folder, checkBox.IsChecked.GetValueOrDefault());
        }
    }

    [DynamicWindowsRuntimeCast(typeof(CheckBox))]
    private async void JumpListCheckboxToggled(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.Tag is IMailItemFolder folder)
        {
            await ViewModel.FolderJumpListToggledAsync(folder, checkBox.IsChecked.GetValueOrDefault());
        }
    }

    [DynamicWindowsRuntimeCast(typeof(CheckBox))]
    private void JumpListCheckBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox)
        {
            checkBox.IsEnabled = ViewModel.IsJumpListEnabled;
        }
    }

    [DynamicWindowsRuntimeCast(typeof(ToggleSwitch))]
    private void JumpListMasterToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggleSwitch)
        {
            return;
        }

        foreach (var checkBox in this.FindDescendants<CheckBox>())
        {
            if (checkBox.Name == "FolderJumpListCheckBox")
            {
                checkBox.IsEnabled = toggleSwitch.IsOn;
            }
        }
    }

    private async void CalendarSynchronizationToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch { Tag: AccountCalendarSettingsItemViewModel calendarItem } toggleSwitch)
        {
            calendarItem.IsSynchronizationEnabled = toggleSwitch.IsOn;
            await ViewModel.UpdateCalendarSynchronizationAsync(calendarItem.Calendar, toggleSwitch.IsOn);
        }
    }

    private async void CalendarShowAsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { Tag: AccountCalendarSettingsItemViewModel calendarItem, SelectedItem: AccountCalendarShowAsOption option })
        {
            calendarItem.SelectedShowAsOption = option;
            await ViewModel.UpdateCalendarDefaultShowAsAsync(calendarItem.Calendar, option);
        }
    }

    private async void CalendarColorItemClick(object sender, ItemClickEventArgs e)
    {
        if (sender is GridView { Tag: AccountCalendarSettingsItemViewModel calendarItem } && e.ClickedItem is AppColorViewModel color)
        {
            await ViewModel.UpdateCalendarColorAsync(calendarItem, color);
        }
    }

}
