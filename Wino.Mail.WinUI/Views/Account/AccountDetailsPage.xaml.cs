using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wino.Core.Domain.Models.Folders;
using Wino.Mail.ViewModels;
using Wino.Views.Abstract;

namespace Wino.Views;

public sealed partial class AccountDetailsPage : AccountDetailsPageAbstract
{
    public AccountDetailsPage()
    {
        InitializeComponent();

        NavigationCacheMode = NavigationCacheMode.Enabled;
    }

    private void OnTabSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs e)
    {
        ViewModel.SelectedTabIndex = TabSelector.SelectedItem == null ? 1 : TabSelector.Items.IndexOf(TabSelector.SelectedItem);
    }

    private async void SyncFolderToggled(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.Tag is IMailItemFolder folder)
        {
            await ViewModel.FolderSyncToggledAsync(folder, checkBox.IsChecked.GetValueOrDefault());
        }
    }

    private async void UnreadBadgeCheckboxToggled(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.Tag is IMailItemFolder folder)
        {
            await ViewModel.FolderShowUnreadToggled(folder, checkBox.IsChecked.GetValueOrDefault());
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

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        var targetTabIndex = ViewModel.SelectedTabIndex;
        if (targetTabIndex < 0 || targetTabIndex >= TabSelector.Items.Count)
        {
            targetTabIndex = 1;
        }

        TabSelector.SelectedItem = TabSelector.Items[targetTabIndex];
    }
}
