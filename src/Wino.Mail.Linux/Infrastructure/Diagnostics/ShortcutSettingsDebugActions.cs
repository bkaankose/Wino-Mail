#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Linux.Views.Dialogs;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-shortcut-recorder</c> (<c>as</c>) drives the shortcut dialog: <c>['open']</c> or <c>['edit', Mode, Action]</c>
/// open it through the dialog service (the result is logged); <c>['select', Mode, Action]</c>, <c>['arm']</c>,
/// <c>['press', accelerator]</c> (the recorder's own key path), <c>['confirm']</c>, <c>['cancel']</c> act on the open
/// dialog; <c>['state', path]</c> writes mode|action|key|modifiers|listening|error. To save through the page's flow,
/// click the page's Add or Edit button and drive the dialog with these.
/// </summary>
internal static class ShortcutSettingsDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-shortcut-recorder", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) => Observe(RunAsync(services, args.Parameter?.GetStrv(out _) ?? []));
        application.AddAction(action);
    }

    private static async Task RunAsync(Func<IServiceProvider?> services, string[] parts)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        var op = parts.Length > 0 ? parts[0] : string.Empty;
        var dialog = KeyboardShortcutDialog.DebugCurrent;
        switch (op)
        {
            case "open":
            case "edit" when parts.Length >= 3:
            {
                var existing = op == "edit"
                    ? (await provider.GetRequiredService<IKeyboardShortcutService>().GetKeyboardShortcutsAsync())
                        .FirstOrDefault(s => s.Mode == Parse<WinoApplicationMode>(parts[1]) && s.Action == Parse<KeyboardShortcutAction>(parts[2]))
                      ?? throw new InvalidOperationException("No such shortcut.")
                    : null;
                var result = await provider.GetRequiredService<IMailDialogService>().ShowKeyboardShortcutDialogAsync(existing);
                Serilog.Log.Information("Debug action: shortcut dialog result {Mode}|{Key}|{Modifiers}|{Action}|{Success}.",
                    result.Mode, result.Key, result.ModifierKeys, result.Action, result.IsSuccess);
                return;
            }
        }

        if (dialog is null) throw new InvalidOperationException("No shortcut dialog is open.");
        switch (op)
        {
            case "select" when parts.Length >= 3:
                dialog.DebugSelect(Parse<WinoApplicationMode>(parts[1]), Parse<KeyboardShortcutAction>(parts[2]));
                break;
            case "arm":
                dialog.DebugArm();
                break;
            case "press" when parts.Length >= 2:
                if (!ShortcutKeys.TryParseAccelerator(parts[1], out var keyval, out var state)) throw new ArgumentException($"'{parts[1]}' is not an accelerator.");
                var handled = dialog.HandleKey(keyval, state);
                Serilog.Log.Information("Debug action: recorder key {Accelerator} handled {Handled}, state {State}.", parts[1], handled, dialog.DebugState());
                break;
            case "confirm":
                dialog.DebugConfirm();
                break;
            case "cancel":
                dialog.DebugCancel();
                break;
            case "state" when parts.Length >= 2:
                await File.WriteAllTextAsync(parts[1] + ".partial", dialog.DebugState());
                File.Move(parts[1] + ".partial", parts[1], true);
                break;
            default:
                throw new ArgumentException($"Unknown debug-shortcut-recorder operation '{string.Join(' ', parts)}'.");
        }
    }

    private static T Parse<T>(string value) where T : struct, Enum
        => Enum.TryParse<T>(value, true, out var parsed) ? parsed : throw new ArgumentException($"'{value}' is not a {typeof(T).Name}.");

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-shortcut-recorder failed."); }
    }
}
#endif
