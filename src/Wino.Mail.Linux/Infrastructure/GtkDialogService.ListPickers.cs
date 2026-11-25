using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Contacts;
using Wino.Mail.Controls.Linux.Common;
using Wino.Mail.Linux.Views.Dialogs;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure;

// Account, calendar, task list and contact pickers on ListPickerDialog, and the new contact list form.
internal sealed partial class GtkDialogService
{
    public async Task<MailAccount?> PickAccountAsync(IReadOnlyList<MailAccount> accounts, string title)
    {
        var items = (accounts ?? Array.Empty<MailAccount>()).ToArray();
        if (items.Length <= 1) return items.Length == 1 ? items[0] : null;
        var picked = await PresentAsync(owner => ListPickerDialog.PresentAsync(owner,
            new ListPickerRequest(title ?? string.Empty, Array.ConvertAll(items, AccountRow), Translator.Buttons_OK)), ListPickerResult.Dismissed);
        return picked.Index >= 0 ? items[picked.Index] : null;
    }

    public async Task<MailAccount?> ShowAccountPickerDialogAsync(List<MailAccount> availableAccounts)
    {
        var items = (availableAccounts ?? []).ToArray();
        if (items.Length == 0) return null;
        var picked = await PresentAsync(owner => ListPickerDialog.PresentAsync(owner,
            new ListPickerRequest(Translator.AccountPickerDialog_Title, Array.ConvertAll(items, AccountRow), Translator.Buttons_Continue) { InitialIndex = 0 }),
            ListPickerResult.Dismissed);
        return picked.Index >= 0 ? items[picked.Index] : null;
    }

    public async Task<AccountCalendarPickingResult> ShowSingleCalendarPickerDialogAsync(List<CalendarPickerAccountGroup> availableCalendarGroups)
    {
        var rows = new List<ListPickerRow>();
        var calendars = new List<AccountCalendar?>();
        foreach (var group in availableCalendarGroups ?? [])
        {
            if (group is null) continue;
            rows.Add(new ListPickerRow(group.Account is { } account ? AccountLine(account) : string.Empty) { IsHeading = true });
            calendars.Add(null);
            foreach (var calendar in group.Calendars ?? [])
            {
                if (calendar is null) continue;
                rows.Add(new ListPickerRow(calendar.Name ?? string.Empty)
                {
                    ColorDotHex = calendar.BackgroundColorHex,
                    IsEnabled = !calendar.IsReadOnly,
                    DisabledReason = calendar.IsReadOnly ? Translator.CalendarPicker_ReadOnly : null,
                });
                calendars.Add(calendar);
            }
        }

        var picked = await PresentAsync(owner => ListPickerDialog.PresentAsync(owner,
            new ListPickerRequest(Translator.CalendarEventCompose_PickCalendarTitle, [.. rows], Translator.Buttons_OK)
            {
                Description = Translator.CalendarEventCompose_DefaultCalendarHint,
                FooterLinkText = Translator.CalendarEventCompose_DefaultCalendarSettingsLink,
            }), ListPickerResult.Dismissed);
        var chosen = picked.Index >= 0 && picked.Index < calendars.Count ? calendars[picked.Index] : null;
        return new AccountCalendarPickingResult(chosen, picked.FooterLinkActivated);
    }

    public Task<AccountTaskList?> ShowTaskListPickerDialogAsync(IReadOnlyList<AccountTaskList> taskLists, IReadOnlyList<MailAccount> accounts)
        => PickTaskListAsync(taskLists, accounts, Translator.ToDoSettings_ListPicker_Title);

    public Task<AccountTaskList?> ShowTaskListPickerDialogAsync(IReadOnlyList<AccountTaskList> taskLists, IReadOnlyList<MailAccount> accounts, string title)
        => PickTaskListAsync(taskLists, accounts, string.IsNullOrWhiteSpace(title) ? Translator.ToDoSettings_ListPicker_Title : title);

    private async Task<AccountTaskList?> PickTaskListAsync(IReadOnlyList<AccountTaskList> taskLists, IReadOnlyList<MailAccount> accounts, string title)
    {
        var lists = (taskLists ?? Array.Empty<AccountTaskList>()).Where(list => list is not null).ToArray();
        if (lists.Length == 0) return null;
        var owners = accounts ?? Array.Empty<MailAccount>();
        var rows = Array.ConvertAll(lists, list => new ListPickerRow(list.Title ?? string.Empty)
        {
            Subtitle = owners.FirstOrDefault(account => account?.Id == list.MailAccountId) is { } owner ? AccountLine(owner) : null,
            Glyph = WinoIconGlyph.TaskList,
            GlyphTintHex = list.ColorHex,
            IsEnabled = !list.IsReadOnly,
            DisabledReason = list.IsReadOnly ? Translator.CalendarPicker_ReadOnly : null,
        });
        var initial = Array.FindIndex(lists, list => list.IsDefault && !list.IsReadOnly);
        if (initial < 0) initial = Array.FindIndex(lists, list => !list.IsReadOnly);
        var picked = await PresentAsync(owner => ListPickerDialog.PresentAsync(owner,
            new ListPickerRequest(title, rows, Translator.Buttons_OK) { InitialIndex = initial }),
            ListPickerResult.Dismissed);
        return picked.Index >= 0 ? lists[picked.Index] : null;
    }

    public async Task<ContactCreateDestination?> ShowContactDestinationPickerDialogAsync(IReadOnlyList<ContactCreateDestination> destinations)
    {
        var items = (destinations ?? Array.Empty<ContactCreateDestination>()).Where(destination => destination is not null).ToArray();
        if (items.Length == 0) return null;
        var rows = Array.ConvertAll(items, destination => new ListPickerRow(destination.AddressBookName ?? string.Empty)
        {
            Subtitle = destination.AccountName,
            Glyph = WinoIconGlyph.People,
            IsEnabled = !destination.IsReadOnly,
            DisabledReason = destination.IsReadOnly ? Translator.CalendarPicker_ReadOnly : null,
        });
        var initial = Array.FindIndex(items, destination => destination.IsDefault && !destination.IsReadOnly);
        if (initial < 0) initial = Array.FindIndex(items, destination => !destination.IsReadOnly);
        var picked = await PresentAsync(owner => ListPickerDialog.PresentAsync(owner,
            new ListPickerRequest(Translator.PeopleSettings_DestinationPicker_Title, rows, Translator.Buttons_OK) { InitialIndex = initial }),
            ListPickerResult.Dismissed);
        return picked.Index >= 0 ? items[picked.Index] : null;
    }

    public Task<ContactListCreationResult?> ShowNewContactListDialogAsync(IReadOnlyList<MailAccount> accounts, MailAccount? selectedAccount)
    {
        var available = (accounts ?? Array.Empty<MailAccount>()).Where(account => account is not null).ToArray();
        if (available.Length == 0) return Task.FromResult<ContactListCreationResult?>(null);
        return PresentAsync<ContactListCreationResult?>(async owner =>
        {
            var form = new FormDialog(Translator.ContactList_NewTitle, null, Translator.Buttons_Create);
            var name = Adw.EntryRow.New();
            name.SetUseMarkup(false);
            name.SetTitle(Translator.ContactList_NameHeader);
            name.SetActivatesDefault(true);
            var account = Adw.ComboRow.New();
            account.SetUseMarkup(false);
            account.SetTitle(Translator.ContactList_AccountHeader);
            account.SetModel(Gtk.StringList.New(Array.ConvertAll(available, AccountLine)));
            var preselected = selectedAccount is null ? 0 : Math.Max(0, Array.FindIndex(available, item => item.Id == selectedAccount.Id));
            account.SetSelected((uint)preselected);
            var group = form.AddGroup();
            group.Add(name);
            group.Add(account);

            MailAccount? Selected()
            {
                var index = account.GetSelected();
                return index < available.Length ? available[index] : null;
            }

            form.CanConfirm = () => !string.IsNullOrWhiteSpace(name.GetText()) && Selected() is not null;
            form.Own(GtkSignals.OnChanged(name, form.Refresh));
            form.Own(GtkSignals.OnNotify(account, "selected", form.Refresh));
            form.SetInitialFocus(name);
#if DEBUG
            _openContactListForm = (name, form);
#endif
            var confirmed = await form.PresentAsync(owner);
#if DEBUG
            _openContactListForm = null;
#endif
            return confirmed && Selected() is { } chosen ? new ContactListCreationResult(name.GetText().Trim(), chosen) : null;
        }, null);
    }

#if DEBUG
    private static (Adw.EntryRow Name, FormDialog Form)? _openContactListForm;

    internal static bool DebugContactListName(string text, bool confirm)
    {
        if (_openContactListForm is not { } open) return false;
        open.Name.SetText(text);
        if (confirm) open.Form.DebugConfirm();
        return true;
    }
#endif

    /// <summary>"Name · address", or whichever of the two exists.</summary>
    private static string AccountLine(MailAccount account)
    {
        var name = account.Name?.Trim() ?? string.Empty;
        var address = account.DisplayAddress?.Trim() ?? string.Empty;
        if (name.Length == 0) return address;
        if (address.Length == 0 || string.Equals(name, address, StringComparison.OrdinalIgnoreCase)) return name;
        return $"{name} · {address}";
    }

    private ListPickerRow AccountRow(MailAccount account)
    {
        var name = account.Name?.Trim() ?? string.Empty;
        var address = account.DisplayAddress?.Trim() ?? string.Empty;
        var title = name.Length > 0 ? name : address;
        var pictures = services.GetService<IPictureStorageService>();
        return new ListPickerRow(title)
        {
            Subtitle = address.Length > 0 && !string.Equals(address, title, StringComparison.OrdinalIgnoreCase) ? address : null,
            Glyph = account.IsLocalOnly ? WinoIconGlyph.Laptop : WinoIconGlyph.Person,
            GlyphTintHex = account.AccountColorHex,
            Prefix = pictures is null ? null : () => new WinoAccountIcon(28)
            {
                Account = MailAccountIconInfoFactory.Create(account, pictures),
                AccessibleName = title,
            }.Root,
        };
    }
}
