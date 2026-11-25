#if DEBUG
using Wino.Mail.Linux.Views.Mail;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-mail-search-filters</c> <c>as</c> on <see cref="MailListView.DebugCurrent"/>: <c>['open']</c> (on the Filters
/// token when it is shown, else the filter button), <c>['state', path]</c>, <c>['set', field, value]</c> (writes the control),
/// <c>['search']</c>, <c>['reset']</c>, <c>['cancel']</c>, <c>['shot', png]</c>.
/// </summary>
internal static class MailSearchFilterDebugActions
{
    public static void Register(Adw.Application application)
    {
        var action = Gio.SimpleAction.New("debug-mail-search-filters", GLib.VariantType.New("as"));
        action.OnActivate += (_, args) =>
        {
            try { Run(args.Parameter?.GetStrv(out var _) ?? []); }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-mail-search-filters failed."); }
        };
        application.AddAction(action);
    }

    private static void Run(string[] arguments)
    {
        var command = Argument(arguments, 0);
        var view = MailListView.DebugCurrent ?? throw new InvalidOperationException("No mail list is shown.");
        switch (command)
        {
            case "open":
                var token = view.DebugScopeBar is { IsFiltersVisible: true } bar && bar.Root.GetVisible() ? bar.FiltersButton : null;
                view.ShowSearchFilters(token ?? view.DebugFilterAnchor ?? throw new InvalidOperationException("No anchor."));
                break;
            case "state":
            {
                var vm = view.ViewModel;
                var line = $"{view.SearchFilterPopover?.Describe() ?? "closed"}\nfilters={vm.ActiveSearchFilterCount} {vm.SearchFilters}\nlive={WinoLiveObjects.Instances(nameof(MailSearchFilterPopover)).Length}\n";
                DebugActions.WriteText(Argument(arguments, 1), line);
                break;
            }
            case "set":
                if (view.SearchFilterPopover?.Set(Argument(arguments, 1), Argument(arguments, 2)) != true) throw new ArgumentException("No open popover or unknown field.");
                break;
            case "search": (view.SearchFilterPopover ?? throw new InvalidOperationException("Not open.")).Search(); break;
            case "reset": (view.SearchFilterPopover ?? throw new InvalidOperationException("Not open.")).Reset(); break;
            case "shot":
                PopoverDebug.Capture((view.SearchFilterPopover ?? throw new InvalidOperationException("Not open.")).Widget, Argument(arguments, 1));
                break;
            case "cancel": (view.SearchFilterPopover ?? throw new InvalidOperationException("Not open.")).Close(); break;
            default: throw new ArgumentException($"'{command}' is not a debug-mail-search-filters command.");
        }
        Serilog.Log.Information("Debug action: mail search filters {Command}", command);
    }

    private static string Argument(string[] arguments, int index)
        => index < arguments.Length ? arguments[index] : throw new ArgumentException($"Argument {index} is missing.");
}
#endif
