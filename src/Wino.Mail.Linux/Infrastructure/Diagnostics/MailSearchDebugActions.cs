#if DEBUG
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Controls.Linux.MailList;
using Wino.Mail.Linux.Views.Mail;
using Wino.Mail.ViewModels.Search;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-mail-search</c> <c>as</c> on <see cref="MailListView.DebugCurrent"/>: <c>['submit', text]</c> (also shows the
/// text in the header entry), <c>['type', text]</c>, <c>['clear']</c>, <c>['done']</c>, <c>['reach', 'local'|'online']</c>,
/// <c>['scope', '0'|'1'|'2']</c>, <c>['chip', 'unread'|'attachments'|'flagged'|'clear-all']</c>,
/// <c>['remove', 'sender'|'subject'|'date'|'read']</c>, <c>['filter-sender', address]</c>, <c>['query', text]</c> (sets the
/// ViewModel's query as another surface does), <c>['online']</c>, <c>['state', path]</c>.
/// </summary>
internal static class MailSearchDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-mail-search", GLib.VariantType.New("as"));
        action.OnActivate += (_, args) => Run(args.Parameter?.GetStrv(out var _) ?? [], services);
        application.AddAction(action);
    }

    private static async void Run(string[] arguments, Func<IServiceProvider?> services)
    {
        try
        {
            var command = Argument(arguments, 0);
            var view = MailListView.DebugCurrent ?? throw new InvalidOperationException("No mail list is shown.");
            var bar = view.DebugScopeBar ?? throw new InvalidOperationException("No scope bar.");
            var target = (Infrastructure.IShellSearchTarget)view;
            switch (command)
            {
                case "submit":
                    services()?.GetService<GtkNavigationService>()?.Shell?.SetSearchText(Argument(arguments, 1));
                    await target.SearchSubmittedAsync(Argument(arguments, 1));
                    break;
                case "type": await target.SearchTextChangedAsync(Argument(arguments, 1)); break;
                case "clear": await target.SearchClearedAsync(); break;
                case "done": await view.DebugDoneAsync(); break;
                case "reach":
                    bar.IsOnline = Argument(arguments, 1) == "online";
                    await view.DebugRerunSearchAsync();
                    break;
                case "scope": view.DebugChangeScope(int.Parse(Argument(arguments, 1), CultureInfo.InvariantCulture)); break;
                case "chip":
                {
                    var name = Argument(arguments, 1);
                    if (name == "clear-all")
                    {
                        view.DebugClearAll();
                        break;
                    }
                    var kind = name switch
                    {
                        "unread" => MailSearchFilterKind.ReadStatus,
                        "attachments" => MailSearchFilterKind.Attachments,
                        "flagged" => MailSearchFilterKind.Flagged,
                        _ => throw new ArgumentException($"'{name}' is not a toggle."),
                    };
                    var chip = bar.Chips.First(chip => chip.Tag == (int)kind && !chip.IsRemovable);
                    await view.DebugToggleChipAsync(chip);
                    break;
                }
                case "remove":
                {
                    var kind = Argument(arguments, 1) switch
                    {
                        "sender" => MailSearchFilterKind.Sender,
                        "subject" => MailSearchFilterKind.Subject,
                        "date" => MailSearchFilterKind.Date,
                        "read" => MailSearchFilterKind.ReadStatus,
                        var other => throw new ArgumentException($"'{other}' is not a value filter."),
                    };
                    var chip = bar.Chips.First(chip => chip.Tag == (int)kind && chip.IsRemovable);
                    await view.DebugToggleChipAsync(chip);
                    break;
                }
                case "filter-sender":
                    view.ViewModel.SearchFilters = (view.ViewModel.SearchFilters ?? MailSearchFilters.Empty) with { Sender = Argument(arguments, 1) };
                    await view.DebugRerunSearchAsync();
                    break;
                case "query": view.ViewModel.SearchQuery = Argument(arguments, 1); break;
                case "online": await view.DebugRunOnlineSearchAsync(); break;
                case "state": DebugActions.WriteText(Argument(arguments, 1), State(view, bar)); break;
                default: throw new ArgumentException($"'{command}' is not a debug-mail-search command.");
            }
            Serilog.Log.Information("Debug action: mail search {Command}", command);
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-mail-search failed."); }
    }

    private static string State(MailListView view, WinoSearchScopeBar bar)
    {
        var vm = view.ViewModel;
        var text = new StringBuilder();
        text.Append("searchText=").Append(view.DebugSearchText).Append('\n')
            .Append("query=").Append(vm.SearchQuery).Append('\n')
            .Append("inSearch=").Append(vm.IsInSearchMode).Append(" online=").Append(vm.AreSearchResultsOnline)
            .Append(" onlineButton=").Append(vm.IsOnlineSearchButtonVisible).Append(" panel=").Append(view.DebugOnlinePanelVisible).Append('\n')
            .Append("scope=").Append(vm.SearchScope).Append(" barScope=").Append(bar.SelectedScope).Append(" reachOnline=").Append(bar.IsOnline).Append('\n')
            .Append("filters=").Append(vm.ActiveSearchFilterCount).Append(' ').Append(vm.SearchFilters).Append('\n')
            .Append("bar=").Append(bar.Root.GetVisible()).Append(" status='").Append(bar.StatusText).Append("' clearAll=").Append(bar.IsClearAllVisible)
            .Append(" filtersToken=").Append(bar.IsFiltersVisible).Append('\n')
            .Append("rows=").Append(view.RowCount).Append(" mails=").Append(vm.MailCollection.Count).Append('\n');
        foreach (var chip in bar.Chips)
            text.Append("chip=").Append(chip.Text).Append('|').Append(chip.IsActive).Append('|').Append(chip.IsRemovable).Append('|').Append(chip.RemoveLabel).Append('\n');
        for (var index = 0; index < view.RowCount; index++)
        {
            if (view.EntryAt((uint)index)?.Row.SourceItem is Wino.Mail.ViewModels.Data.MailItemViewModel item) text.Append("row=").Append(item.Subject).Append('\n');
        }
        return text.ToString();
    }

    private static string Argument(string[] arguments, int index)
        => index < arguments.Length ? arguments[index] : throw new ArgumentException($"Argument {index} is missing.");
}
#endif
