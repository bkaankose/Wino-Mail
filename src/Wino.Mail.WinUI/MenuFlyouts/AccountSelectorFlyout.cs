using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AccountIcon;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.Controls;
using Wino.Mail.WinUI;

namespace Wino.MenuFlyouts;

public partial class AccountSelectorFlyout : WinoMenuFlyout, IDisposable
{
    private readonly IEnumerable<MailAccount> _accounts;
    private readonly Func<MailAccount, Task> _onItemSelection;

    public AccountSelectorFlyout(IEnumerable<MailAccount> accounts, Func<MailAccount, Task> onItemSelection)
    {
        _accounts = accounts;
        _onItemSelection = onItemSelection;
        var profilePictureService = WinoApplication.Current.Services.GetRequiredService<IAccountProfilePictureFileService>();

        foreach (var account in _accounts)
        {
            var menuItem = new MenuFlyoutItem
            {
                Tag = account.Address,
                Icon = new WinoAccountIcon
                {
                    Account = MailAccountIconInfoFactory.Create(account, profilePictureService),
                    IconSize = 28,
                },
                Text = $"{account.Name} ({account.Address})",
                MinHeight = 55,
            };

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

    private async void AccountClicked(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
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
