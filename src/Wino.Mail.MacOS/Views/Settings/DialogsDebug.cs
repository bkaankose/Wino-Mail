#if DEBUG
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Contacts;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.MacOS.Infrastructure;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Debug bridge commands that open the core dialogs for snapshots: <c>dlg-sysfolders</c>, <c>dlg-calpicker</c>,
/// <c>dlg-contactdest</c>, <c>dlg-tasklist</c>, <c>dlg-newlist</c>, <c>dlg-alias</c> and <c>dlg-category [edit]</c>.
/// Each command only presents the sheet and returns; the result is logged and never written anywhere,
/// except the system folder sheet, whose Save runs the real flow when a person chooses it.
/// </summary>
internal static class DialogsDebug
{
    private static IServiceProvider Services => typeof(MacDebugBridge).GetField("_services", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as IServiceProvider
        ?? throw new InvalidOperationException("no service provider");

    private static IMailDialogService Dialogs => Services.GetRequiredService<IMailDialogService>();

    public static void Register()
    {
        MacDebugBridge.Register("dlg-sysfolders", async _ =>
        {
            var account = await FirstAccountAsync();
            if (account is null) return "no account";
            Show(() => Dialogs.HandleSystemFolderConfigurationDialogAsync(account.Id, Services.GetRequiredService<IFolderService>()));
            return "ok";
        });

        MacDebugBridge.Register("dlg-calpicker", async _ =>
        {
            var calendars = Services.GetRequiredService<ICalendarService>();
            var groups = new List<CalendarPickerAccountGroup>();
            foreach (var account in await AccountsAsync())
                groups.Add(new CalendarPickerAccountGroup { Account = account, Calendars = await calendars.GetAccountCalendarsAsync(account.Id) });
            Show(async () =>
            {
                var result = await Dialogs.ShowSingleCalendarPickerDialogAsync(groups);
                return $"calendar={result.PickedCalendar?.Name ?? "none"} settings={result.ShouldNavigateToCalendarSettings}";
            });
            return "ok";
        });

        MacDebugBridge.Register("dlg-contactdest", async _ =>
        {
            // Placeholder destinations built from the real accounts: one default, one read-only per account.
            var destinations = (await AccountsAsync()).SelectMany((account, index) => new[]
            {
                new ContactCreateDestination(account.Id, Guid.NewGuid(), ContactSourceKind.Local, account.Name, "Contacts", index == 0),
                new ContactCreateDestination(account.Id, Guid.NewGuid(), ContactSourceKind.Local, account.Name, "Directory", false, true),
            }).ToList();
            Show(async () => $"destination={(await Dialogs.ShowContactDestinationPickerDialogAsync(destinations))?.DisplayName ?? "none"}");
            return "ok";
        });

        MacDebugBridge.Register("dlg-tasklist", async _ =>
        {
            var accounts = await AccountsAsync();
            var lists = accounts.SelectMany((account, index) => new[]
            {
                new AccountTaskList { MailAccountId = account.Id, Title = "Tasks", IsDefault = index == 0, ColorHex = "#0F6CBD" },
                new AccountTaskList { MailAccountId = account.Id, Title = "Errands", ColorHex = "#C239B3" },
            }).ToList();
            Show(async () => $"list={(await Dialogs.ShowTaskListPickerDialogAsync(lists, accounts))?.Title ?? "none"}");
            return "ok";
        });

        MacDebugBridge.Register("dlg-newlist", async _ =>
        {
            var accounts = await AccountsAsync();
            Show(async () =>
            {
                var result = await Dialogs.ShowNewContactListDialogAsync(accounts, accounts.FirstOrDefault());
                return result is null ? "cancelled" : $"name={result.Name} account={result.Account.Name}";
            });
            return "ok";
        });

        MacDebugBridge.Register("dlg-alias", _ =>
        {
            Show(async () =>
            {
                var result = await Dialogs.ShowCreateAccountAliasDialogAsync();
                return result.CreatedAccountAlias is { } alias ? $"alias={alias.AliasAddress} replyTo={alias.ReplyToAddress}" : "cancelled";
            });
            return Task.FromResult("ok");
        });

        MacDebugBridge.Register("dlg-category", args =>
        {
            var existing = args.Length > 0 && args[0].Equals("edit", StringComparison.OrdinalIgnoreCase)
                ? new MailCategory { Id = Guid.NewGuid(), Name = "Project Falcon", BackgroundColorHex = MailCategoryPalette.DefaultOptions[8].BackgroundColorHex, TextColorHex = MailCategoryPalette.DefaultOptions[8].TextColorHex }
                : null;
            Show(async () =>
            {
                var result = await Dialogs.ShowEditMailCategoryDialogAsync(existing!);
                return result is null ? "cancelled" : $"name={result.Name} background={result.BackgroundColorHex} text={result.TextColorHex}";
            });
            return Task.FromResult("ok");
        });
    }

    private static async Task<List<MailAccount>> AccountsAsync() => await Services.GetRequiredService<IAccountService>().GetAccountsAsync() ?? [];

    private static async Task<MailAccount?> FirstAccountAsync() => (await AccountsAsync()).FirstOrDefault();

    /// <summary>The sheet stays open while the bridge returns, so its outcome goes to the console only.</summary>
    private static async void Show(Func<Task<string>> present)
    {
        try { Console.WriteLine($"[dialogs-debug] {await present()}"); }
        catch (Exception exception) { Console.WriteLine($"[dialogs-debug] failed: {exception.Message}"); }
    }

    private static void Show(Func<Task> present) => Show(async () => { await present(); return "closed"; });
}
#endif
