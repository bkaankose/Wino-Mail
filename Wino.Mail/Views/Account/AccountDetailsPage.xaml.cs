using Windows.UI.Xaml.Controls;
using Wino.Core.Domain.Models.Folders;
using Wino.Views.Abstract;

namespace Wino.Views;

public sealed partial class AccountDetailsPage : AccountDetailsPageAbstract
{
    public AccountDetailsPage()
    {
        InitializeComponent();
    }

    private async void SyncFolderToggled(object sender, Windows.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.Tag is IMailItemFolder folder)
        {
            await ViewModel.FolderSyncToggledAsync(folder, checkBox.IsChecked.GetValueOrDefault());
        }
    }

    private async void UnreadBadgeCheckboxToggled(object sender, Windows.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is CheckBox checkBox && checkBox.Tag is IMailItemFolder folder)
        {
            await ViewModel.FolderShowUnreadToggled(folder, checkBox.IsChecked.GetValueOrDefault());
        }
    }
}
