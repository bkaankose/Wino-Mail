using AppKit;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>The account choice behind New mail when several accounts qualify (Windows AccountSelectorFlyout).</summary>
public sealed partial class AppKitDialogService
{
    // Beyond this NSAlert stacks the buttons into a column taller than the sheet reads well; a pop-up list takes over.
    private const int MaxAccountButtons = 4;

    public async Task<MailAccount?> PickAccountAsync(IReadOnlyList<MailAccount> accounts, string title)
    {
        if (accounts.Count <= 1) return accounts.Count == 1 ? accounts[0] : null;

        if (accounts.Count <= MaxAccountButtons)
        {
            var buttons = new string[accounts.Count + 1];
            for (int i = 0; i < accounts.Count; i++) buttons[i] = AccountLabel(accounts[i]);
            buttons[^1] = Translator.Buttons_Cancel;
            var index = await AlertAsync(title, string.Empty, buttons);
            return index >= 0 && index < accounts.Count ? accounts[index] : null;
        }

        NSPopUpButton? popup = null;
        await dispatcher.ExecuteOnUIThread(() =>
        {
            popup = new NSPopUpButton(new CGRect(0, 0, 320, 26), false);
            // Menu items, not AddItem: AddItem drops entries whose titles repeat, and two accounts can share a name.
            foreach (var account in accounts) popup.Menu?.AddItem(new NSMenuItem(AccountLabel(account)));
            popup.SelectItem(0);
        });
        try
        {
            if (await AlertAsync(title, string.Empty, [Translator.Buttons_OK, Translator.Buttons_Cancel], popup) != 0) return null;
            var selected = -1;
            await dispatcher.ExecuteOnUIThread(() => selected = (int)(popup?.IndexOfSelectedItem ?? -1));
            return selected >= 0 && selected < accounts.Count ? accounts[selected] : null;
        }
        finally { await dispatcher.ExecuteOnUIThread(() => popup?.Dispose()); }
    }

    private static string AccountLabel(MailAccount account)
    {
        var name = account.Name?.Trim();
        var address = account.Address?.Trim();
        if (string.IsNullOrEmpty(name)) return address ?? string.Empty;
        if (string.IsNullOrEmpty(address) || string.Equals(name, address, StringComparison.OrdinalIgnoreCase)) return name;
        return $"{name} ({address})";
    }
}
