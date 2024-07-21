using Wino.Views.Abstract;
using Wino.Domain.Models.Folders;


#if NET8_0
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
#else
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
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
