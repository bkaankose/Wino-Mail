using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Wino.Controls;
using Wino.Helpers;
using Wino.Domain.Entities;


#if NET8_0
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
#else
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
#endif

namespace Wino.MenuFlyouts
{
    public class AccountSelectorFlyout : MenuFlyout, IDisposable
    {
        private readonly IEnumerable<MailAccount> _accounts;
        private readonly Func<MailAccount, Task> _onItemSelection;

        public AccountSelectorFlyout(IEnumerable<MailAccount> accounts, Func<MailAccount, Task> onItemSelection)
        {
            _accounts = accounts;
            _onItemSelection = onItemSelection;

            foreach (var account in _accounts)
            {
                var pathData = new WinoFontIcon() { Icon = XamlHelpers.GetProviderIcon(account.ProviderType) };
                var menuItem = new MenuFlyoutItem() { Tag = account.Address, Icon = pathData, Text = $"{account.Name} ({account.Address})", MinHeight = 55 };

                menuItem.Click += AccountClicked;
                Items.Add(menuItem);
            }
        }

        public void Dispose()
        {
            foreach (var menuItem in Items)
            {
                if (menuItem is MenuFlyoutItem flyoutItem)
                {
                    flyoutItem.Click -= AccountClicked;
                }
            }
        }

        private async void AccountClicked(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem menuItem && menuItem.Tag is string accountAddress)
            {
                var selectedMenuItem = _accounts.FirstOrDefault(a => a.Address == accountAddress);

                if (selectedMenuItem != null)
                {
                    await _onItemSelection(selectedMenuItem);
                }
            }

            Dispose();
            Hide();
        }
    }
}
