#if DEBUG
using Wino.Mail.Controls.Linux.Compose;
using Wino.Mail.Linux.Views.Mail.Compose;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-recipients</c> <c>as</c> on the active composer's token fields (to, cc, bcc): <c>['type', field, text]</c> (sets the
/// entry text: the paste split path runs), <c>['commit', field]</c>, <c>['backspace', field]</c>,
/// <c>['menu', field, index, copy|edit|remove|to|cc|bcc]</c>, <c>['drag', from, index, to]</c> (drops the chip's payload),
/// <c>['clipboard']</c> (logs the clipboard text), <c>['dump', path]</c>.
/// </summary>
internal static class RecipientDebugActions
{
    public static void Register(Adw.Application application)
    {
        var action = Gio.SimpleAction.New("debug-recipients", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) => _ = RunAsync(args.Parameter?.GetStrv(out var _) ?? []);
        application.AddAction(action);
    }

    private static async Task RunAsync(string[] arguments)
    {
        try
        {
            var command = Argument(arguments, 0);
            var view = ComposeDebugActions.Current ?? throw new InvalidOperationException("No composer is shown.");
            switch (command)
            {
                case "type":
                    if (Argument(arguments, 1) != "to") view.ViewModel.IsCCBCCVisible = true;
                    Box(view, Argument(arguments, 1)).Entry.SetText(Argument(arguments, 2));
                    break;
                case "commit": Log($"commit {Argument(arguments, 1)}={Box(view, Argument(arguments, 1)).CommitTyped()}"); break;
                case "backspace": Box(view, Argument(arguments, 1)).HandleBackspace(); break;
                case "menu": Box(view, Argument(arguments, 1)).RunChipCommand(int.Parse(Argument(arguments, 2)), Argument(arguments, 3)); break;
                case "drag":
                {
                    var from = Box(view, Argument(arguments, 1));
                    var payload = from.DragPayload(int.Parse(Argument(arguments, 2)));
                    Log($"drop accepted={Box(view, Argument(arguments, 3)).AcceptDrop(payload)}");
                    break;
                }
                case "clipboard":
                    var text = await view.Root.GetClipboard().ReadTextAsync();
                    Log($"clipboard '{text}'");
                    break;
                case "dump":
                {
                    await view.SyncAllRecipientsAsync(false);
                    var vm = view.ViewModel;
                    static string Join(IEnumerable<Wino.Core.Domain.Entities.Shared.AccountContact> items) => string.Join(",", items.Select(static c => c.Address));
                    var lines = $"{view.ToBox}\n{view.CcBox}\n{view.BccBox}\nvm to={Join(vm.ToItems)} cc={Join(vm.CCItems)} bcc={Join(vm.BCCItems)} ccbcc={vm.IsCCBCCVisible}\n" +
                        $"error to={view.ToBox.Entry.HasCssClass("error")} chips={WinoLiveObjects.Instances(nameof(WinoRecipientChip)).Length} boxes={WinoLiveObjects.Instances(nameof(WinoRecipientTokenBox)).Length}\n";
                    DebugActions.WriteText(Argument(arguments, 1), lines);
                    break;
                }
                default: throw new ArgumentException($"'{command}' is not a debug-recipients command.");
            }
            Log("done " + command);
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-recipients failed."); }
    }

    private static WinoRecipientTokenBox Box(ComposeView view, string field) => field switch
    {
        "to" => view.ToBox,
        "cc" => view.CcBox,
        "bcc" => view.BccBox,
        _ => throw new ArgumentException($"'{field}' is not a recipient field.")
    };

    private static void Log(string line) => Serilog.Log.Information("recipients-debug: {Line}", line);

    private static string Argument(string[] arguments, int index)
        => index < arguments.Length ? arguments[index] : throw new ArgumentException($"Argument {index} is missing.");
}
#endif
