#if DEBUG
using Wino.Mail.Linux.Views.Mail;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-mail-actionbar</c> <c>as</c> on <see cref="MailListView.DebugCurrent"/>: <c>['state', path]</c>
/// (preference, visibility, action item and selection counts, then the bar's own lines), <c>['pref', 'on'|'off']</c>,
/// <c>['labels', 'on'|'off']</c>, <c>['run', operation]</c> (clicks that command's button; a hidden one runs from More),
/// <c>['more']</c> (opens the More menu).
/// </summary>
internal static class MailActionBarDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-mail-actionbar", GLib.VariantType.New("as"));
        action.OnActivate += (_, args) =>
        {
            try
            {
                var arguments = args.Parameter?.GetStrv(out var _) ?? [];
                if (arguments.Length == 0) throw new ArgumentException("Expected a command.");
                var view = MailListView.DebugCurrent ?? throw new InvalidOperationException("No mail list is shown.");
                var preferences = view.ViewModel.PreferencesService;
                switch (arguments[0])
                {
                    case "state":
                        DebugActions.WriteText(Argument(arguments, 1),
                            $"pref={preferences.IsMailListActionBarEnabled} visible={view.IsActionBarVisible} items={view.ViewModel.ActionItems.Count} selected={view.ViewModel.SelectedItemsCount}\n{view.ActionBar?.Describe()}");
                        break;
                    case "pref": preferences.IsMailListActionBarEnabled = Argument(arguments, 1) == "on"; break;
                    case "labels": preferences.IsShowActionLabelsEnabled = Argument(arguments, 1) == "on"; break;
                    case "run":
                    {
                        var bar = view.ActionBar ?? throw new InvalidOperationException("The action bar was never shown.");
                        var anchor = bar.AnchorFor(Argument(arguments, 1));
                        if (anchor is Gtk.Button button && !ReferenceEquals(anchor, bar.MoreButton)) button.Activate();
                        else
                        {
                            bar.OpenMore();
                            MainLoop.Post(() =>
                            {
                                if (ContextMenu.Current is { } menu) PopoverDebug.Click(menu, Title(arguments[1]));
                            });
                        }
                        break;
                    }
                    case "more": view.ActionBar?.OpenMore(); break;
                    default: throw new ArgumentException($"Unknown command '{arguments[0]}'.");
                }
                Serilog.Log.Information("Debug action: action bar {Command}", string.Join(' ', arguments));
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-mail-actionbar failed."); }
        };
        application.AddAction(action);
    }

    private static string Title(string operation)
        => Enum.TryParse<Wino.Core.Domain.Enums.MailOperation>(operation, out var parsed)
            ? parsed == Wino.Core.Domain.Enums.MailOperation.SetFlag ? Wino.Core.Domain.Translator.MailOperation_Flag : Wino.Core.Domain.Presentation.MailOperationDisplay.GetTitle(parsed)
            : operation;

    private static string Argument(string[] arguments, int index)
        => arguments.Length > index ? arguments[index] : throw new ArgumentException($"Expected argument {index}.");
}
#endif
