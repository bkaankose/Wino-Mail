using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Contacts;
using Wino.Mail.UIKit.Views.Dialogs;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>The new contact list form (the Mac AppKitDialogService.ContactsTasks).</summary>
public sealed partial class UIKitDialogService
{
    /// <summary>Windows NewContactListDialog: list name, then the owning account; Create stays disabled until both are set.</summary>
    public Task<ContactListCreationResult?> ShowNewContactListDialogAsync(IReadOnlyList<MailAccount> accounts, MailAccount? selectedAccount)
        => PresentAsync<ContactListCreationResult?>(async presenter =>
        {
            var available = (accounts ?? []).ToList();
            var form = new FormSheet(Translator.ContactList_NewTitle, null, Translator.Buttons_Create);
            var name = form.AddTextField(Translator.ContactList_NameHeader, string.Empty);
            var preselected = selectedAccount is null ? 0 : Math.Max(0, available.FindIndex(account => account.Id == selectedAccount.Id));
            var account = form.AddPopUp(Translator.ContactList_AccountHeader, available.Select(AccountLine), preselected);

            MailAccount? SelectedAccount()
            {
                var index = account.SelectedIndex;
                return index >= 0 && index < available.Count ? available[index] : null;
            }

            form.CanConfirm = () => !string.IsNullOrWhiteSpace(name.Text) && SelectedAccount() is not null;
            if (!await form.PresentAsync(presenter)) return null;
            return SelectedAccount() is { } owner ? new ContactListCreationResult((name.Text ?? string.Empty).Trim(), owner) : null;
        }, null);

    /// <summary>"Name · address", or whichever of the two exists.</summary>
    private static string AccountLine(MailAccount account)
    {
        var name = account.Name?.Trim() ?? string.Empty;
        var address = account.Address?.Trim() ?? string.Empty;
        if (name.Length == 0) return address;
        if (address.Length == 0 || string.Equals(name, address, StringComparison.OrdinalIgnoreCase)) return name;
        return $"{name} · {address}";
    }
}
