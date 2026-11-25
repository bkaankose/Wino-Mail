#if DEBUG
using System.Globalization;
using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Mail;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-mail-swipe</c> <c>as</c> on <see cref="MailListView.DebugCurrent"/> (a row is <c>#n</c> or a subject):
/// <c>['progress', row, n]</c> (the row at n of the threshold as a moving finger puts it; negative = left),
/// <c>['release', row]</c> (lifting the finger), <c>['cancel']</c>, <c>['set', 'enabled'|'left'|'right', value]</c>
/// (preferences), <c>['state', path]</c> (<c>subject|active|side|progress|armed|offset|hover shown</c> per bound cell).
/// </summary>
internal static class MailSwipeDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-mail-swipe", GLib.VariantType.New("as"));
        action.OnActivate += (_, args) =>
        {
            try
            {
                var arguments = args.Parameter?.GetStrv(out var _) ?? [];
                if (arguments.Length == 0) throw new ArgumentException("Expected a command.");
                var view = MailListView.DebugCurrent ?? throw new InvalidOperationException("No mail list is shown.");
                view.DebugSwipeCommitted ??= (operation, count) => Serilog.Log.Information("Debug action: swipe committed {Operation} on {Count} mails", operation, count);
                var preferences = view.ViewModel.PreferencesService;
                var result = string.Empty;
                switch (arguments[0])
                {
                    case "progress":
                    {
                        var cell = Cell(view, Argument(arguments, 1));
                        var value = double.Parse(Argument(arguments, 2), CultureInfo.InvariantCulture);
                        cell.Swipe.SetProgress(value);
                        result = cell.Swipe.IsActive
                            ? $"side={cell.Swipe.Side} progress={cell.Swipe.Progress:F2} armed={cell.Swipe.IsArmed} operation={view.ViewModel.ResolveSwipeOperation(cell.Item ?? throw new InvalidOperationException("Unbound."), cell.Swipe.Side == Wino.Mail.Controls.Linux.MailList.WinoSwipeSide.Leading)}"
                            : "no action";
                        break;
                    }
                    case "release":
                    {
                        var cell = Cell(view, Argument(arguments, 1));
                        var armed = cell.Swipe.IsArmed;
                        cell.Swipe.Release();
                        result = armed ? "committing" : "cancelled";
                        break;
                    }
                    case "cancel": view.DebugSwipingCell?.Swipe.Cancel(); break;
                    case "set":
                    {
                        var value = Argument(arguments, 2);
                        switch (Argument(arguments, 1))
                        {
                            case "enabled": preferences.IsSwipeActionsEnabled = value == "true"; break;
                            case "left": preferences.LeftSwipeOperation = Enum.Parse<MailOperation>(value); break;
                            case "right": preferences.RightSwipeOperation = Enum.Parse<MailOperation>(value); break;
                            default: throw new ArgumentException("Unknown preference.");
                        }
                        break;
                    }
                    case "state": DebugActions.WriteText(Argument(arguments, 1), view.DebugSwipeState()); break;
                    default: throw new ArgumentException($"Unknown command '{arguments[0]}'.");
                }
                Serilog.Log.Information("Debug action: swipe {Command} {Result}", string.Join(' ', arguments), result);
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-mail-swipe failed."); }
        };
        application.AddAction(action);
    }

    private static MailRowCell Cell(MailListView view, string selector)
    {
        var row = view.DebugFindRow(selector) ?? throw new ArgumentException($"No row '{selector}'.");
        return view.DebugCellOf(row) ?? throw new InvalidOperationException("The row has no cell yet; run the action again.");
    }

    private static string Argument(string[] arguments, int index)
        => arguments.Length > index ? arguments[index] : throw new ArgumentException($"Expected argument {index}.");
}
#endif
