#if DEBUG
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core.HoverActions;
using Wino.Mail.Linux.Views.Mail;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-mail-hover</c> <c>as</c> on <see cref="MailListView.DebugCurrent"/> (a row is <c>#n</c> or a subject):
/// <c>['show', row]</c>, <c>['hide']</c>, <c>['invoke', row, 'Archive'|'Delete'|'ToggleFlag'|'ToggleRead'|'MoveToJunk']</c>
/// (the click path; refused when the row does not show it), <c>['set', 'position'|'size'|'animation'|'enabled'|'left'|'center'|'right', value]</c>
/// (preferences), <c>['state', path]</c> (<c>subject|shown|has actions|labels|bar bounds in the cell</c> per bound cell).
/// </summary>
internal static class MailHoverDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-mail-hover", GLib.VariantType.New("as"));
        action.OnActivate += (_, args) =>
        {
            try
            {
                var arguments = args.Parameter?.GetStrv(out var _) ?? [];
                if (arguments.Length == 0) throw new ArgumentException("Expected a command.");
                var view = MailListView.DebugCurrent ?? throw new InvalidOperationException("No mail list is shown.");
                var preferences = view.ViewModel.PreferencesService;
                var result = string.Empty;
                switch (arguments[0])
                {
                    case "show":
                    {
                        var row = view.DebugFindRow(Argument(arguments, 1)) ?? throw new ArgumentException("No such row.");
                        view.ForceHover(row);
                        var cell = view.DebugHoverCell(row);
                        result = $"shown={cell?.Hover.IsShown} labels={cell?.Hover.Describe()}";
                        break;
                    }
                    case "hide": view.ForceHover(null); break;
                    case "invoke":
                    {
                        var row = view.DebugFindRow(Argument(arguments, 1)) ?? throw new ArgumentException("No such row.");
                        if (!Enum.TryParse<HoverActionKind>(Argument(arguments, 2), out var kind)) throw new ArgumentException("Unknown hover action.");
                        var cell = view.DebugHoverCell(row) ?? throw new InvalidOperationException("The row has no cell.");
                        if (!cell.Hover.DebugShows(kind)) throw new InvalidOperationException($"The row does not show {kind}.");
                        cell.Hover.DebugInvoke(kind);
                        break;
                    }
                    case "set":
                    {
                        var value = Argument(arguments, 2);
                        switch (Argument(arguments, 1))
                        {
                            case "position": preferences.HoverActionPosition = Enum.Parse<MailHoverActionPosition>(value); break;
                            case "size": preferences.HoverActionButtonSize = Enum.Parse<MailHoverActionButtonSize>(value); break;
                            case "animation": preferences.HoverActionAnimation = Enum.Parse<MailHoverActionAnimation>(value); break;
                            case "enabled": preferences.IsHoverActionsEnabled = value == "true"; break;
                            case "left": preferences.LeftHoverAction = Enum.Parse<MailOperation>(value); break;
                            case "center": preferences.CenterHoverAction = Enum.Parse<MailOperation>(value); break;
                            case "right": preferences.RightHoverAction = Enum.Parse<MailOperation>(value); break;
                            default: throw new ArgumentException("Unknown preference.");
                        }
                        break;
                    }
                    case "state": DebugActions.WriteText(Argument(arguments, 1), view.DebugHoverState()); break;
                    default: throw new ArgumentException($"Unknown command '{arguments[0]}'.");
                }
                Serilog.Log.Information("Debug action: hover {Command} {Result}", string.Join(' ', arguments), result);
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-mail-hover failed."); }
        };
        application.AddAction(action);
    }

    private static string Argument(string[] arguments, int index)
        => arguments.Length > index ? arguments[index] : throw new ArgumentException($"Expected argument {index}.");
}
#endif
