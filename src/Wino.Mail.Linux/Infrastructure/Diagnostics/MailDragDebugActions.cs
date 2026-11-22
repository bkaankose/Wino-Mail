#if DEBUG
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.Linux.Views.Mail;
using Wino.Mail.Linux.Views.Shell;
using Wino.Mail.ViewModels.MenuItems.Contracts;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-mail-drag</c> <c>as</c>: the pointer drag cannot be synthesised on Wayland, so these run the same drag and
/// drop code with the mail list's selection as the dragged mails (sidebar rows as in <c>debug-sidebar</c>).
/// <c>['validate', row, path]</c> and <c>['drop', row, path]</c> begin a drag and validate or drop it (the drop path
/// resolves the token and moves); <c>['drop-text', row, text, path]</c> drops a foreign string;
/// <c>['highlight', row|'off']</c>; <c>['banner', n|'off']</c>; <c>['dim', 'on'|'off']</c>;
/// <c>['card', count, caption, 'true'|'false']</c> shows the drag card in a window titled <c>Wino drag card</c>;
/// <c>['autoscroll', 'up'|'down'|'off', path]</c>; <c>['state', path]</c>. Results go to the path.
/// </summary>
internal static class MailDragDebugActions
{
    private const string CardWindowTitle = "Wino drag card";

    private static Adw.Window? _cardWindow;

    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-mail-drag", GLib.VariantType.New("as"));
        action.OnActivate += (_, args) =>
        {
            var arguments = args.Parameter?.GetStrv(out var _) ?? [];
            try { Run(application, arguments, services); }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-mail-drag failed."); }
        };
        application.AddAction(action);
    }

    private static void Run(Adw.Application application, string[] arguments, Func<IServiceProvider?> services)
    {
        var command = Argument(arguments, 0);
        var sidebar = services()?.GetService<GtkNavigationService>()?.Shell?.Sidebar ?? throw new InvalidOperationException("The shell is not shown.");
        var view = MailListView.DebugCurrent;
        switch (command)
        {
            case "validate":
            {
                var item = SidebarDebugActions.Resolve(sidebar, Argument(arguments, 1));
                var list = view ?? throw new InvalidOperationException("No mail list is shown.");
                if (list.DebugBeginDrag() is null) throw new InvalidOperationException("Select a mail first.");
                var verdict = sidebar.DebugValidateDrop(item);
                var line = string.Create(CultureInfo.InvariantCulture,
                    $"action={(verdict.Action == default ? "None" : verdict.Action.ToString())} caption='{verdict.Caption}' refused={verdict.Refused} moveTarget={(item as IBaseFolderMenuItem)?.IsMoveTarget} dragged={MailDragPayload.Current.Count}");
                MailDragPayload.End();
                Write(arguments, 2, line);
                break;
            }
            case "drop":
            case "drop-text":
            {
                var item = SidebarDebugActions.Resolve(sidebar, Argument(arguments, 1));
                var list = view ?? throw new InvalidOperationException("No mail list is shown.");
                var token = list.DebugBeginDrag() ?? throw new InvalidOperationException("Select a mail first.");
                var dragged = MailDragPayload.Current.Count;
                var text = command == "drop" ? token : Argument(arguments, 2);
                var taken = sidebar.DebugDrop(item, text);
                MailDragPayload.End();
                Write(arguments, command == "drop" ? 2 : 3, string.Create(CultureInfo.InvariantCulture, $"taken={taken} dragged={dragged}"));
                break;
            }
            case "highlight":
            {
                var row = Argument(arguments, 1);
                sidebar.DebugHighlight(row == "off" ? null : SidebarDebugActions.Resolve(sidebar, row));
                break;
            }
            case "banner":
            {
                var list = view ?? throw new InvalidOperationException("No mail list is shown.");
                var value = Argument(arguments, 1);
                if (value == "off") list.ViewModel.SetDragState(false);
                else list.ViewModel.SetDragState(true, int.Parse(value, CultureInfo.InvariantCulture));
                break;
            }
            case "dim":
            {
                var list = view ?? throw new InvalidOperationException("No mail list is shown.");
                if (Argument(arguments, 1) == "on")
                {
                    if (list.DebugBeginDrag() is null) throw new InvalidOperationException("Select a mail first.");
                }
                else MailDragPayload.End();
                break;
            }
            case "card":
                ShowCard(application, view ?? throw new InvalidOperationException("No mail list is shown."),
                    int.Parse(Argument(arguments, 1), CultureInfo.InvariantCulture), Argument(arguments, 2), Argument(arguments, 3) == "true");
                break;
            case "autoscroll":
            {
                var direction = Argument(arguments, 1);
                sidebar.DebugAutoscroll(direction switch { "up" => -12, "down" => 12, _ => 0 });
                var adjustment = sidebar.Scroller.GetVadjustment();
                var before = adjustment.GetValue();
                MainLoop.Delay(500, () =>
                {
                    Write(arguments, 2, string.Create(CultureInfo.InvariantCulture,
                        $"before={before:0} after={adjustment.GetValue():0} upper={adjustment.GetUpper():0} page={adjustment.GetPageSize():0} speed={sidebar.AutoscrollSpeed}"));
                });
                break;
            }
            case "state":
            {
                var drop = "none";
                foreach (var item in sidebar.Rows)
                {
                    if (sidebar.DropRow is { } row && sidebar.CellOf(item)?.Root.GetParent() is { } parent && ReferenceEquals(parent, row))
                        drop = sidebar.Describe(item).Content?.Title ?? "?";
                }
                Write(arguments, 1, string.Create(CultureInfo.InvariantCulture,
                    $"dragged={MailDragPayload.Current.Count} inProgress={view?.ViewModel.IsDragInProgress} banner={view?.DebugBannerVisible}:'{view?.DebugBannerText}' dimmed={view?.DebugDimmedCount()} dropRow={drop}"));
                break;
            }
            default: throw new ArgumentException($"'{command}' is not a debug-mail-drag command.");
        }
        Serilog.Log.Information("Debug action: mail drag {Command}", command);
    }

    private static void ShowCard(Adw.Application application, MailListView view, int count, string caption, bool refused)
    {
        var card = view.DebugCreateCard(count) ?? throw new InvalidOperationException("Select a visible mail first.");
        card.SetCaption(caption, refused);
        if (_cardWindow is null)
        {
            var window = Adw.Window.New();
            window.SetApplication(application);
            window.SetTitle(CardWindowTitle);
            window.SetDefaultSize(360, 180);
            window.OnCloseRequest += (_, _) =>
            {
                if (ReferenceEquals(_cardWindow, window)) _cardWindow = null;
                return false;
            };
            _cardWindow = window;
        }
        var holder = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        holder.SetMarginTop(24);
        holder.SetMarginStart(24);
        holder.Append(card.Root);
        _cardWindow.SetContent(holder);
        _cardWindow.Present();
    }

    private static void Write(string[] arguments, int index, string line)
    {
        if (index < arguments.Length && arguments[index].Length > 0) DebugActions.WriteText(arguments[index], line + "\n");
        Serilog.Log.Information("Debug action: mail drag {Line}", line);
    }

    private static string Argument(string[] arguments, int index)
        => index < arguments.Length ? arguments[index] : throw new ArgumentException($"Argument {index} is missing.");
}
#endif
