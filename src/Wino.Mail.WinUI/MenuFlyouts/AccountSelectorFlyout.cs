using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain.Entities.Shared;
using Wino.Mail.Controls.ContextFlyout;
using Wino.Mail.Controls.Core.ContextFlyout;

namespace Wino.MenuFlyouts;

public partial class AccountSelectorFlyout : WinoContextFlyout, IDisposable
{
    private readonly IEnumerable<MailAccount> _accounts;
    private readonly Func<MailAccount, Task> _onItemSelection;

    public AccountSelectorFlyout(IEnumerable<MailAccount> accounts, Func<MailAccount, Task> onItemSelection)
    {
        _accounts = accounts.ToArray();
        _onItemSelection = onItemSelection;

        ItemsSource = _accounts
            .Select(account => (ContextFlyoutMenuEntry)new ContextFlyoutCommandEntry
            {
                Text = $"{account.Name} ({account.Address})",
                Icon = new ContextFlyoutIcon("\uE77B"),
                Command = new AsyncRelayCommand(() => SelectAccountAsync(account)),
                AutomationId = $"AccountSelector_{account.Id:N}"
            })
            .ToArray();
    }

    public void Dispose() => Hide();

    private async Task SelectAccountAsync(MailAccount account)
    {
        await _onItemSelection(account);
        Dispose();
    }
}
