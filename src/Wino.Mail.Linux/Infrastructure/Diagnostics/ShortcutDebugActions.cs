#if DEBUG
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-shortcut</c> <c>as</c>: keys cannot be synthesised on Wayland, so every step of the router runs from here.
/// <c>['fire', Action]</c>, <c>['key', accelerator(, hardware keycode)]</c> (the key path with the real focus: configurable,
/// fixed and single keys; a key no shortcut takes goes to the focused list's, sidebar's or search entry's own handler, as
/// the real key would), <c>['match', accelerator, path]</c> (now, in text input, without focus), <c>['list', path]</c> (every
/// keymap entry and its state), <c>['focus', label|'type:Gtk.ListView'|'pane:sidebar|content|detail|trailing'|'search']</c>,
/// <c>['pane', path]</c> (the focused pane and the panes F6 visits), <c>['single', 'on'|'off'|'default']</c>,
/// <c>['overview']</c>, <c>['set', Mode, Action, accelerator|'off'|'on']</c>.
/// </summary>
internal static class ShortcutDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-shortcut", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) =>
        {
            try
            {
                var arguments = args.Parameter?.GetStrv(out var _) ?? [];
                if (arguments.Length == 0) throw new ArgumentException("An operation is required.");
                var provider = services() ?? throw new InvalidOperationException("The services are not ready.");
                Run(provider, arguments);
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-shortcut failed."); }
        };
        application.AddAction(action);
    }

    private static void Run(IServiceProvider services, string[] arguments)
    {
        var host = services.GetRequiredService<ShortcutHost>();
        var router = host.MainRouter ?? throw new InvalidOperationException("The main window has no shortcut router.");
        var window = services.GetRequiredService<GtkNavigationService>().MainWindow?.Window ?? throw new InvalidOperationException("There is no main window.");
        var op = arguments[0];
        switch (op)
        {
            case "fire":
            {
                if (!Enum.TryParse<KeyboardShortcutAction>(Argument(arguments, 1), true, out var shortcutAction)) throw new ArgumentException("Unknown action.");
                _ = FireAsync(router, shortcutAction);
                return;
            }
            case "key":
            {
                var (pressed, state) = Parse(Argument(arguments, 1));
                var keycode = arguments.Length > 2 && uint.TryParse(arguments[2], out var code) ? code : 0;
                var focus = window.GetFocus();
                var result = router.Route(pressed, keycode, state, focus, simulated: true, out var matched);
                var widget = result == ShortcutRouteResult.Dispatched ? "-" : WidgetKey(services, focus, pressed, state);
                Report(op, $"{result} action={matched} widget={widget} focus={focus?.GetType().FullName ?? "-"} after={window.GetFocus()?.GetType().FullName ?? "-"}");
                return;
            }
            case "pane":
            {
                var shell = services.GetRequiredService<GtkNavigationService>().Shell ?? throw new InvalidOperationException("There is no shell.");
                var mail = Views.Mail.MailListView.DebugCurrent;
                var text = $"{shell.DescribeFocus()} rows={mail?.SelectedRowCount.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} at={mail?.DebugSelectedPositions ?? "-"} single={services.GetRequiredService<SingleKeyShortcuts>().IsEnabled}\n";
                DebugActions.WriteText(Argument(arguments, 1), text);
                Report(op, text.TrimEnd());
                return;
            }
            case "single":
            {
                var single = services.GetRequiredService<SingleKeyShortcuts>();
                switch (Argument(arguments, 1))
                {
                    case "on": single.Set(true); break;
                    case "off": single.Set(false); break;
                    default: single.ClearChoice(); break;
                }
                Report(op, $"enabled={single.IsEnabled} chosen={single.HasChoice}");
                return;
            }
            case "match":
            {
                var (keyval, state) = Parse(Argument(arguments, 1));
                var now = router.Resolve(keyval, state, window.GetFocus(), out var shortcut);
                var text = router.Resolve(keyval, state, Gtk.Text.New(), out _);
                var none = router.Resolve(keyval, state, null, out _);
                var mode = services.GetRequiredService<GtkNavigationService>().ActiveMode?.ToString() ?? "-";
                DebugActions.WriteText(Argument(arguments, 2), $"{shortcut?.Action.ToString() ?? "-"}|{mode}|{now}|{text}|{none}\n");
                Report(op, $"{shortcut?.Action.ToString() ?? "-"} now={now} text={text} none={none}");
                return;
            }
            case "list":
            {
                var builder = new StringBuilder();
                if (services.GetRequiredService<GtkNavigationService>().ActiveMode is { } mode)
                {
                    builder.Append("Mode=").AppendLine(mode.ToString());
                    foreach (var shortcut in router.ShortcutsFor(mode))
                        builder.Append(shortcut.Action).Append('=').AppendLine(ShortcutKeys.Accelerator(shortcut.ModifierKeys, shortcut.Key) ?? "-");
                }
                var application = window.GetApplication() ?? throw new InvalidOperationException("The window has no application.");
                foreach (var key in LinuxKeymap.All)
                {
                    var accels = key.Scope == LinuxKeyScope.Global ? string.Join(',', application.GetAccelsForAction(AppActions.Detailed(key.Action))) : string.Join(',', key.Accelerators);
                    var action = key.Scope == LinuxKeyScope.SingleKey ? key.Configurable?.ToString() ?? key.Action : "app." + key.Action;
                    var registered = key.Scope == LinuxKeyScope.SingleKey || application.LookupAction(key.Action) is not null;
                    builder.Append(key.Scope).Append(' ').Append(action).Append('=').Append(accels).Append(registered ? string.Empty : " (no action)").Append(" | ").AppendLine(key.Title());
                }
                builder.Append("focus=").AppendLine(window.GetFocus()?.GetType().FullName ?? "-");
                DebugActions.WriteText(Argument(arguments, 1), builder.ToString());
                Report(op, Argument(arguments, 1));
                return;
            }
            case "focus":
            {
                var target = Argument(arguments, 1);
                if (target.StartsWith("pane:", StringComparison.Ordinal))
                {
                    var shell = services.GetRequiredService<GtkNavigationService>().Shell ?? throw new InvalidOperationException("There is no shell.");
                    var pane = Enum.Parse<Views.Shell.ShellPane>(target[5..], ignoreCase: true);
                    Report(op, $"{shell.FocusPane(pane)} {shell.DescribeFocus()}");
                    return;
                }
                var widget = target == "search" ? services.GetRequiredService<GtkNavigationService>().Shell?.DebugHeader.DebugSearchEntry
                    : target.StartsWith("type:", StringComparison.Ordinal) ? FindType(window, target[5..]) : DebugActions.FindWidget(target);
                if (widget is null) throw new InvalidOperationException($"No widget '{target}'.");
                var grabbed = widget.GrabFocus();
                Report(op, $"{grabbed} focus={window.GetFocus()?.GetType().FullName ?? "-"}");
                return;
            }
            case "overview":
                host.ShowOverview();
                Report(op, "shown");
                return;
            case "set":
                _ = SetAsync(services.GetRequiredService<IKeyboardShortcutService>(), arguments);
                return;
            default:
                throw new ArgumentException($"Unknown operation '{op}'.");
        }
    }

    private static async Task FireAsync(ShortcutRouter router, KeyboardShortcutAction action)
    {
        try { Report("fire", $"{action} -> {(await router.DispatchActionAsync(action) ? "dispatched" : "refused")}"); }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-shortcut fire failed."); }
    }

    private static async Task SetAsync(IKeyboardShortcutService service, string[] arguments)
    {
        try
        {
            if (!Enum.TryParse<WinoApplicationMode>(Argument(arguments, 1), true, out var mode)) throw new ArgumentException("Unknown mode.");
            if (!Enum.TryParse<KeyboardShortcutAction>(Argument(arguments, 2), true, out var action)) throw new ArgumentException("Unknown action.");
            var value = Argument(arguments, 3);
            var existing = (await service.GetKeyboardShortcutsAsync()).FirstOrDefault(item => item.Mode == mode && item.Action == action)
                ?? throw new InvalidOperationException($"{mode} has no {action} shortcut.");
            if (value is "off" or "on")
            {
                await service.UpdateKeyboardShortcutEnabledAsync(existing.Id, value == "on");
            }
            else
            {
                var (keyval, state) = Parse(value);
                existing.Key = ShortcutKeys.KeyName(keyval) ?? throw new ArgumentException("That key cannot be a shortcut.");
                existing.ModifierKeys = ShortcutKeys.Modifiers(state);
                await service.SaveKeyboardShortcutAsync(existing);
            }
            Report("set", $"{mode} {action} {value}");
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-shortcut set failed."); }
    }

    // What the focused widget does with a key no shortcut took: the handlers the real key reaches next.
    private static string WidgetKey(IServiceProvider services, Gtk.Widget? focus, uint keyval, Gdk.ModifierType state)
    {
        var shell = services.GetRequiredService<GtkNavigationService>().Shell;
        if (shell is null || focus is null) return "-";
        if (Views.Shell.ShellView.Contains(shell.DebugHeader.DebugSearchEntry, focus))
            return $"search:{shell.DebugHeader.DebugSearchKey(keyval)}";
        if (Views.Shell.ShellView.Contains(shell.Sidebar.List, focus))
            return $"sidebar:{shell.Sidebar.DebugKey(keyval, state)}:{shell.Sidebar.DebugFocusedTitle ?? "-"}";
        // Enter on a list row emits the list's own activate-item, as GTK does for the real key.
        if (keyval is Gdk.Constants.KEY_Return or Gdk.Constants.KEY_KP_Enter)
        {
            for (var widget = focus; widget is not null; widget = widget.GetParent())
            {
                if (widget is not Gtk.ListView view || view.GetModel() is not { } model) continue;
                var selected = Wino.Presentation.Linux.Lists.ListViews.SelectedPositions(model);
                if (selected.Length == 0) return "activate:none";
                return $"activate:{selected[0]}:{view.ActivateAction("list.activate-item", GLib.Variant.NewUint32(selected[0]))}";
            }
        }
        if (Views.Mail.MailListView.DebugCurrent is { } list && Views.Shell.ShellView.Contains(list.List, focus))
            return $"list:{list.DebugKey(keyval, state)}";
        return "-";
    }

    private static (uint Keyval, Gdk.ModifierType State) Parse(string accelerator)
        => ShortcutKeys.TryParseAccelerator(accelerator, out var keyval, out var state) ? (keyval, state) : throw new ArgumentException($"'{accelerator}' is not an accelerator.");

    private static string Argument(string[] arguments, int index)
        => index < arguments.Length && !string.IsNullOrWhiteSpace(arguments[index]) ? arguments[index] : throw new ArgumentException($"Argument {index} is missing.");

    private static Gtk.Widget? FindType(Gtk.Widget widget, string typeName)
    {
        if (widget.GetMapped() && widget.GetType().FullName == typeName) return widget;
        for (var child = widget.GetFirstChild(); child is not null; child = child.GetNextSibling())
        {
            if (FindType(child, typeName) is { } found) return found;
        }
        return null;
    }

    private static void Report(string op, string result) => Serilog.Log.Information("Debug action: shortcut {Op} -> {Result}", op, result);
}
#endif
