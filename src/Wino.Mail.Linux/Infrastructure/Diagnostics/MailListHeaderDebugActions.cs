#if DEBUG
using System.Globalization;
using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Mail;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-maillist-header</c> <c>as</c> on <see cref="MailListView.DebugCurrent"/>: <c>['state', path]</c>,
/// <c>['filter', 'Unread'|title]</c>, <c>['sort', 'Sender'|title]</c>, <c>['pivot', index]</c>, <c>['undo']</c> (the Undo
/// button's path), <c>['update', title, message]</c> (the ViewModel's update bar), <c>['menu']</c> (opens the filter menu), <c>['select-all']</c> (activates the box), <c>['empty-junk-button', 'on'|'off']</c> (the preference), <c>['menu-shot', path]</c>, <c>['click', item text]</c>.
/// </summary>
internal static class MailListHeaderDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-maillist-header", GLib.VariantType.New("as"));
        action.OnActivate += (_, args) =>
        {
            try
            {
                var arguments = args.Parameter?.GetStrv(out var _) ?? [];
                if (arguments.Length == 0) throw new ArgumentException("Expected a command.");
                var view = MailListView.DebugCurrent ?? throw new InvalidOperationException("No mail list is shown.");
                switch (arguments[0])
                {
                    case "state":
                        if (arguments.Length < 2) throw new ArgumentException("Expected a path.");
                        DebugActions.WriteText(arguments[1], view.DebugHeaderState() + "\n");
                        break;
                    case "filter": view.DebugFilter(Argument(arguments, 1)); break;
                    case "sort": view.DebugSort(Argument(arguments, 1)); break;
                    case "pivot": view.DebugPivot(int.Parse(Argument(arguments, 1), CultureInfo.InvariantCulture)); break;
                    case "undo": view.DebugUndo(); break;
                    case "select-all": view.DebugClickSelectAll(); break;
                    case "empty-junk-button": view.ViewModel.PreferencesService.IsShowEmptyJunkFolderEnabled = Argument(arguments, 1) == "on"; break;
                    case "menu": view.DebugShowFilterMenu(); break;
                    case "menu-shot": PopoverDebug.Capture(Popover(view), Argument(arguments, 1)); break;
                    case "click": PopoverDebug.Click(Popover(view), Argument(arguments, 1)); break;
                    case "update":
                        view.ViewModel.BarSeverity = InfoBarMessageType.Information;
                        view.ViewModel.BarTitle = Argument(arguments, 1);
                        view.ViewModel.BarMessage = Argument(arguments, 2);
                        view.ViewModel.IsBarOpen = true;
                        break;
                    default: throw new ArgumentException($"Unknown command '{arguments[0]}'.");
                }
                Serilog.Log.Information("Debug action: header {Command} {State}", string.Join(' ', arguments), view.DebugHeaderState());
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-maillist-header failed."); }
        };
        application.AddAction(action);
    }

    private static Gtk.Popover Popover(MailListView view)
        => view.DebugFilterPopover is { IsOpen: true } popover ? popover.Root : throw new InvalidOperationException("The filter menu is not open.");

    private static string Argument(string[] arguments, int index)
        => arguments.Length > index ? arguments[index] : throw new ArgumentException($"Expected argument {index}.");
}
#endif
