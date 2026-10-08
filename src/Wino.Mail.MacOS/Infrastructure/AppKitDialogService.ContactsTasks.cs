using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Contacts;
using Wino.Mail.MacOS.Views.Dialogs;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Contact destination, new contact list and task list pickers. Owned by the core dialogs work (WS4).</summary>
public sealed partial class AppKitDialogService
{
    /// <summary>Windows ContactDestinationPickerDialog: "Account · Address book", read-only books disabled, the default preselected.</summary>
    public Task<ContactCreateDestination?> ShowContactDestinationPickerDialogAsync(IReadOnlyList<ContactCreateDestination> destinations)
    {
        var items = destinations ?? [];
        var initial = items.FirstOrDefault(destination => destination.IsDefault && !destination.IsReadOnly)
            ?? items.FirstOrDefault(destination => !destination.IsReadOnly);
        return PickSheetAsync(Translator.PeopleSettings_DestinationPicker_Title, null, items,
            _ => Array.Empty<ContactCreateDestination>(),
            destination => destination.IsReadOnly ? $"{destination.DisplayName}  ·  {Translator.CalendarPicker_ReadOnly}" : destination.DisplayName,
            _ => WinoIcons.Image(WinoIconGlyph.People, 14),
            _ => null,
            destination => !destination.IsReadOnly,
            Translator.Buttons_OK,
            destination => destination.IsReadOnly ? Translator.CalendarPicker_ReadOnly : null,
            initial);
    }

    /// <summary>Windows NewContactListDialog: list name and owning account; Create stays disabled until both are set.</summary>
    public Task<ContactListCreationResult?> ShowNewContactListDialogAsync(IReadOnlyList<MailAccount> accounts, MailAccount? selectedAccount)
        => PresentAsync<ContactListCreationResult?>(async window =>
        {
            var available = accounts ?? [];
            var form = new FormSheet(Translator.ContactList_NewTitle, null, Translator.Buttons_Create);
            var name = form.AddTextField(Translator.ContactList_NameHeader, string.Empty);
            var preselected = selectedAccount is null ? 0 : Math.Max(0, available.ToList().FindIndex(account => account.Id == selectedAccount.Id));
            var account = form.AddPopUp(Translator.ContactList_AccountHeader, available.Select(AccountLine), preselected);

            MailAccount? SelectedAccount()
            {
                var index = (int)account.IndexOfSelectedItem;
                return index >= 0 && index < available.Count ? available[index] : null;
            }

            form.CanConfirm = () => !string.IsNullOrWhiteSpace(name.StringValue) && SelectedAccount() is not null;
            if (!await form.PresentAsync(window)) return null;
            return SelectedAccount() is { } owner ? new ContactListCreationResult(name.StringValue.Trim(), owner) : null;
        });

    /// <summary>Windows TaskListPickerDialog: "List — Account · address", the default list preselected.</summary>
    public async Task<AccountTaskList?> ShowTaskListPickerDialogAsync(IReadOnlyList<AccountTaskList> taskLists, IReadOnlyList<MailAccount> accounts)
    {
        var items = (taskLists ?? [])
            .Select(list => new TaskListPickerNode(list, (accounts ?? []).FirstOrDefault(account => account.Id == list.MailAccountId)))
            .ToList();
        var initial = items.FirstOrDefault(item => item.List.IsDefault && !item.List.IsReadOnly) ?? items.FirstOrDefault(item => !item.List.IsReadOnly);
        var picked = await PickSheetAsync(Translator.ToDoSettings_ListPicker_Title, null, items,
            _ => Array.Empty<TaskListPickerNode>(),
            item => item.Text,
            _ => WinoIcons.Image(WinoIconGlyph.TaskList, 14),
            item => WinoStyle.FromHexString(item.List.ColorHex),
            item => !item.List.IsReadOnly,
            Translator.Buttons_OK,
            item => item.List.IsReadOnly ? Translator.CalendarPicker_ReadOnly : null,
            initial);
        return picked?.List;
    }

    /// <summary>"Name · address", or whichever of the two exists.</summary>
    private static string AccountLine(MailAccount account)
    {
        var name = account.Name?.Trim() ?? string.Empty;
        var address = account.Address?.Trim() ?? string.Empty;
        if (name.Length == 0) return address;
        if (address.Length == 0 || string.Equals(name, address, StringComparison.OrdinalIgnoreCase)) return name;
        return $"{name} · {address}";
    }

    private sealed class TaskListPickerNode(AccountTaskList list, MailAccount? account)
    {
        public AccountTaskList List { get; } = list;

        public string Text
        {
            get
            {
                var owner = account is null ? string.Empty : AccountLine(account);
                return owner.Length == 0 ? List.Title ?? string.Empty : $"{List.Title} — {owner}";
            }
        }
    }
}
