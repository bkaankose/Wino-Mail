#if DEBUG
using Wino.Mail.Linux.Views.Mail.Compose;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-toolbar</c> <c>as</c> on the active composer's format toolbar: <c>['dump', path]</c> (rows, overflow, toggles, state),
/// <c>['press', label, png?]</c> (activates the item whose tooltip is the label, as a click does; a menu button's popover is captured to png), <c>['select', font|size|paragraph, index]</c>,
/// <c>['tab', index]</c>, <c>['width', px]</c> (pretends the toolbar has that width; 0 = real width), <c>['more', png]</c> (opens the
/// shown row's More menu and captures it), <c>['menu-click', text]</c> (an item of the last line-spacing menu), <c>['more-click', text]</c> (opens More and activates a top-level item), <c>['select-all']</c> (WebKit's SelectAll editing command in the body).
/// </summary>
internal static class ToolbarDebugActions
{
    public static void Register(Adw.Application application)
    {
        var action = Gio.SimpleAction.New("debug-toolbar", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) => Run(args.Parameter?.GetStrv(out var _) ?? []);
        application.AddAction(action);
    }

    private static void Run(string[] arguments)
    {
        try
        {
            var command = Argument(arguments, 0);
            var view = ComposeDebugActions.Current ?? throw new InvalidOperationException("No composer is shown.");
            var toolbar = view.FormatToolbar ?? throw new InvalidOperationException("The composer has no toolbar.");
            switch (command)
            {
                case "dump": DebugActions.WriteText(Argument(arguments, 1), toolbar.Describe() + "\n"); break;
                case "press":
                {
                    var label = Argument(arguments, 1);
                    var item = toolbar.AllItems.FirstOrDefault(item => !item.IsDivider && item.Name == label) ?? throw new ArgumentException($"No toolbar item '{label}'.");
                    if (item.Widget is Gtk.MenuButton menuButton)
                    {
                        menuButton.Popup();
                        if (arguments.Length > 2 && menuButton.GetPopover() is { } popover) PopoverDebug.Capture(popover, arguments[2]);
                    }
                    else item.Widget.Activate();
                    break;
                }
                case "select":
                    if (!toolbar.DebugSelect(Argument(arguments, 1), int.Parse(Argument(arguments, 2)))) throw new ArgumentException($"No drop-down '{arguments[1]}'.");
                    break;
                case "tab": toolbar.SelectTab(int.Parse(Argument(arguments, 1))); break;
                case "width":
                    toolbar.DebugWidth = int.Parse(Argument(arguments, 1));
                    toolbar.DebugRelayout();
                    break;
                case "more":
                {
                    var popover = toolbar.VisibleRow.OpenOverflowMenu() ?? throw new InvalidOperationException("The shown row does not overflow.");
                    PopoverDebug.Capture(popover, Argument(arguments, 1));
                    break;
                }
                case "menu-click": PopoverDebug.Click(toolbar.DebugLastMenu ?? throw new InvalidOperationException("No toolbar menu is open."), Argument(arguments, 1)); break;
                case "more-click":
                    PopoverDebug.Click(toolbar.VisibleRow.OpenOverflowMenu() ?? throw new InvalidOperationException("The shown row does not overflow."), Argument(arguments, 1));
                    break;
                case "select-all": (view.Editor ?? throw new InvalidOperationException("No editor.")).View.ExecuteEditingCommand("SelectAll"); break;
                default: throw new ArgumentException($"'{command}' is not a debug-toolbar command.");
            }
            Serilog.Log.Information("toolbar-debug: done {Command}", command);
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-toolbar failed."); }
    }

    private static string Argument(string[] arguments, int index)
        => index < arguments.Length ? arguments[index] : throw new ArgumentException($"Argument {index} is missing.");
}
#endif
