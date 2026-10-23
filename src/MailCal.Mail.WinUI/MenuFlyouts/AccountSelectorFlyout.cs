using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Mail.Controls.ContextFlyout;
using MailCal.Mail.Controls.Core.ContextFlyout;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Enums;

namespace MailCal.MenuFlyouts;

public partial class AccountSelectorFlyout : MailCalContextFlyout, IDisposable
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
                Icon = new ContextFlyoutIcon(MailCalIconGlyphs.GetGlyph(MailCalIconGlyph.Person)),
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
