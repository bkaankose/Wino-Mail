#if DEBUG
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-shortcut</c> <c>as</c>: keys cannot be synthesised on Wayland, so every step of the router runs from here.
/// <c>['fire', Action]</c>, <c>['key', accelerator]</c> (the key path with the real focus), <c>['match', accelerator, path]</c>
/// (now, in text input, without focus), <c>['list', path]</c>, <c>['focus', label|'type:Gtk.ListView']</c>,
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
                var (keyval, state) = Parse(Argument(arguments, 1));
                var focus = window.GetFocus();
                router.Resolve(keyval, state, focus, out var shortcut);
                var result = router.Route(keyval, state, focus);
                Report(op, $"{result} action={shortcut?.Action.ToString() ?? "-"} focus={focus?.GetType().FullName ?? "-"}");
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
                foreach (var name in new[] { AppActions.Preferences, AppActions.Quit, AppActions.Shortcuts, AppActions.ToggleSidebar, AppActions.Back, "mode-mail", "mode-calendar", "mode-contacts", "mode-tasks" })
                    builder.Append("app.").Append(name).Append('=').AppendLine(string.Join(',', application.GetAccelsForAction(AppActions.Detailed(name))));
                builder.Append("focus=").AppendLine(window.GetFocus()?.GetType().FullName ?? "-");
                DebugActions.WriteText(Argument(arguments, 1), builder.ToString());
                Report(op, Argument(arguments, 1));
                return;
            }
            case "focus":
            {
                var target = Argument(arguments, 1);
                var widget = target.StartsWith("type:", StringComparison.Ordinal) ? FindType(window, target[5..]) : DebugActions.FindWidget(target);
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
