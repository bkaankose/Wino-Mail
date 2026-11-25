#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Contacts;
using Wino.Mail.Linux.Views.Dialogs;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-picker</c> (<c>s</c>): <c>account</c>, <c>accounts</c>, <c>calendar</c>, <c>tasklist</c>, <c>destination</c>,
/// <c>contactlist</c>, <c>many</c> open that picker through the dialog service and log the answer.
/// <c>debug-picker-state</c> (<c>s</c> path) writes the open picker's rows; <c>debug-picker-choose</c> (<c>i</c>) selects
/// and confirms a row; <c>debug-picker-search</c> (<c>s</c>) types into the search entry; <c>debug-picker-contactlist</c>
/// (<c>as</c> [name, 'confirm'|'']) fills the new contact list form.
/// </summary>
internal static class ListPickerDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var open = Gio.SimpleAction.New("debug-picker", GLib.VariantType.New("s"));
        open.OnActivate += (sender, args) => Observe(OpenAsync(services, args.Parameter?.GetString(out _) ?? string.Empty));
        application.AddAction(open);

        var state = Gio.SimpleAction.New("debug-picker-state", GLib.VariantType.New("s"));
        state.OnActivate += (sender, args) =>
        {
            var path = args.Parameter?.GetString(out _) ?? string.Empty;
            DebugActions.WriteText(path, ListPickerDialog.DebugDescribeOpen() ?? "no picker open\n");
            Serilog.Log.Information("Debug action: picker state written to {Path}.", path);
        };
        application.AddAction(state);

        var choose = Gio.SimpleAction.New("debug-picker-choose", GLib.VariantType.New("i"));
        choose.OnActivate += (sender, args) =>
            Serilog.Log.Information("Debug action: picker choose {Result}.", ListPickerDialog.DebugChoose(args.Parameter?.GetInt32() ?? -1));
        application.AddAction(choose);

        var search = Gio.SimpleAction.New("debug-picker-search", GLib.VariantType.New("s"));
        search.OnActivate += (sender, args) =>
            Serilog.Log.Information("Debug action: picker search {Result}.", ListPickerDialog.DebugSearch(args.Parameter?.GetString(out _) ?? string.Empty));
        application.AddAction(search);

        var contactList = Gio.SimpleAction.New("debug-picker-contactlist", GLib.VariantType.New("as"));
        contactList.OnActivate += (sender, args) =>
        {
            var parts = args.Parameter?.GetStrv(out _) ?? [];
            var done = GtkDialogService.DebugContactListName(parts.Length > 0 ? parts[0] : string.Empty, parts.Length > 1 && parts[1] == "confirm");
            Serilog.Log.Information("Debug action: contact list form {Result}.", done);
        };
        application.AddAction(contactList);
    }

    private static async Task OpenAsync(Func<IServiceProvider?> services, string kind)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        var dialogs = provider.GetRequiredService<IMailDialogService>();
        var accounts = await provider.GetRequiredService<IAccountService>().GetAccountsAsync();
        string answer;
        switch (kind)
        {
            case "account":
                answer = (await dialogs.PickAccountAsync(accounts, "Pick"))?.Name ?? "null";
                break;
            case "accounts":
                answer = (await dialogs.ShowAccountPickerDialogAsync(accounts))?.Name ?? "null";
                break;
            case "calendar":
            {
                var calendars = provider.GetRequiredService<ICalendarService>();
                var groups = new List<CalendarPickerAccountGroup>();
                foreach (var account in accounts)
                    groups.Add(new CalendarPickerAccountGroup { Account = account, Calendars = await calendars.GetAccountCalendarsAsync(account.Id) });
                if (groups.Count > 0)
                    groups[0].Calendars.Add(new AccountCalendar { Id = Guid.NewGuid(), Name = "Lab read-only & <shared>", IsReadOnly = true, BackgroundColorHex = "#8E44AD" });
                var result = await dialogs.ShowSingleCalendarPickerDialogAsync(groups);
                answer = $"{result.PickedCalendar?.Name ?? "null"} ShouldNavigateToCalendarSettings={result.ShouldNavigateToCalendarSettings}";
                break;
            }
            case "tasklist":
            {
                var lists = await provider.GetRequiredService<ITaskQueryService>().GetTaskListsAsync();
                if (accounts.Count > 0)
                    lists.Add(new AccountTaskList { Id = Guid.NewGuid(), MailAccountId = accounts[0].Id, Title = "Lab read-only list", IsReadOnly = true, ColorHex = "#E67E22" });
                answer = (await dialogs.ShowTaskListPickerDialogAsync(lists, accounts))?.Title ?? "null";
                break;
            }
            case "destination":
            {
                var destinations = new ContactCreateDestination[]
                {
                    new(Guid.NewGuid(), Guid.NewGuid(), ContactSourceKind.Local, "Lab account", "Lab book one", false),
                    new(Guid.NewGuid(), Guid.NewGuid(), ContactSourceKind.Local, "Lab account", "Lab book two", true),
                    new(Guid.NewGuid(), Guid.NewGuid(), ContactSourceKind.Local, "Lab account", "Lab read-only book", false, true),
                };
                answer = (await dialogs.ShowContactDestinationPickerDialogAsync(destinations))?.AddressBookName ?? "null";
                break;
            }
            case "contactlist":
            {
                var result = await dialogs.ShowNewContactListDialogAsync(accounts, accounts.Count > 0 ? accounts[^1] : null);
                answer = result is null ? "null" : $"{result.Name} / {result.Account.Name}";
                break;
            }
            case "many":
            {
                var many = Enumerable.Range(1, 40).Select(index => new MailAccount
                {
                    Id = Guid.NewGuid(),
                    Name = index == 7 ? "A & <B>" : $"Lab account {index}",
                    Address = $"lab{index}@example.org",
                    AccountColorHex = index % 3 == 0 ? "#2E86DE" : null,
                }).ToList();
                answer = (await dialogs.PickAccountAsync(many, "Many accounts"))?.Name ?? "null";
                break;
            }
            default:
                throw new ArgumentException($"Unknown debug-picker kind '{kind}'.");
        }
        Serilog.Log.Information("Debug action: picker {Kind} answered {Answer}", kind, answer);
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-picker failed."); }
    }
}
#endif
