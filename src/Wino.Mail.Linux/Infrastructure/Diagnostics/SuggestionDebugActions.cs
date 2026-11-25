#if DEBUG
using Wino.Mail.Controls.Linux.Compose;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-suggestions</c> <c>as</c> on the active composer: <c>['query', field, text]</c> (sets the entry text; the real
/// text-changed path runs), <c>['key', down|up|enter|tab|esc]</c> (the entry's key filter), <c>['choose', index]</c>,
/// <c>['remove', index]</c>, <c>['dump', path]</c>, <c>['shot', path]</c>.
/// </summary>
internal static class SuggestionDebugActions
{
    public static void Register(Adw.Application application)
    {
        var action = Gio.SimpleAction.New("debug-suggestions", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) =>
        {
            try { Run(args.Parameter?.GetStrv(out var _) ?? []); }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-suggestions failed."); }
        };
        application.AddAction(action);
    }

    private static void Run(string[] arguments)
    {
        var command = Argument(arguments, 0);
        var view = ComposeDebugActions.Current ?? throw new InvalidOperationException("No composer is shown.");
        var popup = view.Suggestions ?? throw new InvalidOperationException("No suggestion popup.");
        switch (command)
        {
            case "query":
            {
                var box = Argument(arguments, 1) switch { "cc" => view.CcBox, "bcc" => view.BccBox, _ => view.ToBox };
                if (!ReferenceEquals(box, view.ToBox)) view.ViewModel.IsCCBCCVisible = true;
                box.Entry.SetText(Argument(arguments, 2));
                box.Entry.SetPosition(-1);
                break;
            }
            case "key":
            {
                uint keyval = Argument(arguments, 1) switch { "down" => 0xff54, "up" => 0xff52, "enter" => 0xff0d, "tab" => 0xff09, "esc" => 0xff1b, var other => throw new ArgumentException(other) };
                var box = new[] { view.ToBox, view.CcBox, view.BccBox }.FirstOrDefault(static b => b.TypedText.Length > 0) ?? view.ToBox;
                Log($"key {Argument(arguments, 1)} consumed={box.KeyFilter?.Invoke(keyval, 0) ?? false}");
                break;
            }
            case "choose": popup.Choose(int.Parse(Argument(arguments, 1))); break;
            case "remove": popup.Remove(int.Parse(Argument(arguments, 1))); break;
            case "dump":
                DebugActions.WriteText(Argument(arguments, 1), $"{popup.Describe()}\nversion={view.SuggestionVersion} to={string.Join(",", view.ViewModel.ToItems.Select(static c => c.Address))} typed='{view.ToBox.TypedText}' popups={WinoLiveObjects.Instances(nameof(WinoRecipientSuggestionPopup)).Length}\n");
                break;
            case "shot": PopoverDebug.Capture(popup.Widget, Argument(arguments, 1)); break;
            default: throw new ArgumentException($"'{command}' is not a debug-suggestions command.");
        }
        Log("done " + command);
    }

    private static void Log(string line) => Serilog.Log.Information("suggestions-debug: {Line}", line);

    private static string Argument(string[] arguments, int index)
        => index < arguments.Length ? arguments[index] : throw new ArgumentException($"Argument {index} is missing.");
}
#endif
