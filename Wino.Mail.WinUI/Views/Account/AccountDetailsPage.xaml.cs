using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Models.Folders;
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

    private void CalendarItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is CommunityToolkit.WinUI.Controls.SettingsCard settingsCard && settingsCard.CommandParameter is AccountCalendar calendar)
        {
            ViewModel.CalendarItemClickedCommand?.Execute(calendar);
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e.NavigationMode == NavigationMode.New)
        {
            // Set initial tab to Mail (index 1)
            TabSelector.SelectedItem = TabSelector.Items[1];
        }
    }
}
