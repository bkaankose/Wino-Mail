#if DEBUG
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.Mail;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-mail-menu</c> <c>as</c> on <see cref="MailListView.DebugCurrent"/> (a row is <c>#n</c> or a subject):
/// <c>['list', row, path]</c> writes <c>path|title|enabled|separator|checked|accelerator</c> per entry;
/// <c>['open', row, png]</c> opens the menu as the keyboard does and captures it; <c>['run', row, 'Title'|'Parent/Child'|'3.1']</c>
/// runs an entry; <c>['click', text]</c> activates an item of the open menu; <c>['close']</c>; <c>['seed-categories']</c>.
/// </summary>
internal static class MailContextMenuDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-mail-menu", GLib.VariantType.New("as"));
        action.OnActivate += (_, args) =>
        {
            var arguments = args.Parameter?.GetStrv(out var _) ?? [];
            Run(arguments, services);
        };
        application.AddAction(action);
    }

    private static async void Run(string[] arguments, Func<IServiceProvider?> services)
    {
        try
        {
            if (arguments.Length == 0) throw new ArgumentException("Expected a command.");
            var command = arguments[0];
            if (command == "seed-categories")
            {
                await SeedCategoriesAsync(services() ?? throw new InvalidOperationException("No services."));
                return;
            }
            if (command == "close")
            {
                MailListView.DebugCurrent?.CloseContextMenu();
                MainLoop.Delay(300, () => Serilog.Log.Information("Debug action: mail menu close open={Open} live={Live}", ContextMenu.OpenMenus, WinoMenus.LiveMenus));
                return;
            }
            var view = MailListView.DebugCurrent ?? throw new InvalidOperationException("No mail list is shown.");
            if (command == "click")
            {
                PopoverDebug.Click(view.DebugContextPopover ?? throw new InvalidOperationException("No mail menu is open."), Argument(arguments, 1));
                Serilog.Log.Information("Debug action: mail menu click {Item}", arguments[1]);
                return;
            }
            var row = view.DebugFindRow(Argument(arguments, 1)) ?? throw new ArgumentException($"No row '{arguments[1]}'.");
            switch (command)
            {
                case "list":
                {
                    var entries = await view.DebugBuildContextMenuAsync(row) ?? [];
                    var text = new StringBuilder();
                    Write(text, entries, string.Empty);
                    DebugActions.WriteText(Argument(arguments, 2), text.ToString());
                    Serilog.Log.Information("Debug action: mail menu list {Row} targets={Targets} entries={Entries}", arguments[1], view.DebugTargetCount(row), entries.Length);
                    break;
                }
                case "open":
                {
                    var shown = await view.OpenContextMenuAsync(row, null, null);
                    var path = Argument(arguments, 2);
                    Serilog.Log.Information("Debug action: mail menu open {Row} shown={Shown} open={Open}", arguments[1], shown, ContextMenu.OpenMenus);
                    MainLoop.Delay(600, () =>
                    {
                        if (view.DebugContextPopover is { } popover) PopoverDebug.Capture(popover, path);
                    });
                    break;
                }
                case "run":
                {
                    var entries = await view.DebugBuildContextMenuAsync(row) ?? [];
                    var entry = Find(entries, Argument(arguments, 2)) ?? throw new ArgumentException($"No entry '{arguments[2]}'.");
                    if (!entry.IsEnabled || entry.Run is not { } run)
                    {
                        Serilog.Log.Information("Debug action: mail menu run {Entry} refused (disabled or a submenu).", arguments[2]);
                        return;
                    }
                    await run();
                    Serilog.Log.Information("Debug action: mail menu run {Row} {Entry} targets={Targets} done.", arguments[1], arguments[2], view.DebugTargetCount(row));
                    break;
                }
                default: throw new ArgumentException($"Unknown command '{command}'.");
            }
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-mail-menu failed."); }
    }

    private static void Write(StringBuilder text, MailMenuEntry[] entries, string prefix)
    {
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var path = prefix + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            text.Append(path).Append('|').Append(entry.Title).Append('|').Append(entry.IsEnabled).Append('|').Append(entry.IsSeparator)
                .Append('|').Append(entry.IsChecked?.ToString() ?? string.Empty).Append('|').Append(entry.Accelerator ?? string.Empty).Append('\n');
            if (entry.Children is { } children) Write(text, children, path + ".");
        }
    }

    private static MailMenuEntry? Find(MailMenuEntry[] entries, string spec)
    {
        if (spec.Length > 0 && char.IsDigit(spec[0]))
        {
            MailMenuEntry? current = null;
            var level = entries;
            foreach (var part in spec.Split('.'))
            {
                var index = int.Parse(part, System.Globalization.CultureInfo.InvariantCulture) - 1;
                if (index < 0 || index >= level.Length) return null;
                current = level[index];
                level = current.Children ?? [];
            }
            return current;
        }
        var names = spec.Split('/');
        MailMenuEntry? found = null;
        var search = entries;
        foreach (var name in names)
        {
            found = Search(search, name);
            if (found is null) return null;
            search = found.Children ?? [];
        }
        return found;
    }

    private static MailMenuEntry? Search(MailMenuEntry[] entries, string name)
    {
        foreach (var entry in entries)
        {
            if (!entry.IsSeparator && entry.Title == name) return entry;
        }
        return null;
    }

    private static async Task SeedCategoriesAsync(IServiceProvider services)
    {
        var accounts = await services.GetRequiredService<IAccountService>().GetAccountsAsync();
        if (accounts.FirstOrDefault() is not { } account) throw new InvalidOperationException("No account.");
        var categories = services.GetRequiredService<IMailCategoryService>();
        if ((await categories.GetCategoriesAsync(account.Id)).Count > 0)
        {
            Serilog.Log.Information("Debug action: mail menu seed-categories skipped, the account has categories.");
            return;
        }
        await categories.CreateCategoryAsync(new MailCategory { Id = Guid.NewGuid(), MailAccountId = account.Id, Name = "ML Red", BackgroundColorHex = "#E74C3C", TextColorHex = "#FFFFFF", IsFavorite = true });
        await categories.CreateCategoryAsync(new MailCategory { Id = Guid.NewGuid(), MailAccountId = account.Id, Name = "ML Blue", BackgroundColorHex = "#3498DB", TextColorHex = "#FFFFFF", IsFavorite = false });
        Serilog.Log.Information("Debug action: mail menu seed-categories created two categories.");
    }

    private static string Argument(string[] arguments, int index)
        => arguments.Length > index ? arguments[index] : throw new ArgumentException($"Expected argument {index}.");
}
#endif
