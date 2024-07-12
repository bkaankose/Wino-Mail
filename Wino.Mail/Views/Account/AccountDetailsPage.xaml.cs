using Wino.Core.Domain.Models.Folders;
using Wino.Views.Abstract;

#if NET8_0
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
#else
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml;
#endif

namespace Wino.Views
{
    public sealed partial class AccountDetailsPage : AccountDetailsPageAbstract
    {
        public AccountDetailsPage()
        {
            InitializeComponent();
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
    }
}
