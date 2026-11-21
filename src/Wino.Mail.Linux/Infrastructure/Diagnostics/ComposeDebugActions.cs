#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Mail.Compose;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-compose</c> <c>as</c> on the active composer: <c>['new']</c> (the NewMail shortcut action), <c>['to'|'cc'|'bcc', addresses]</c>,
/// <c>['subject', text]</c>, <c>['body', html]</c>, <c>['get-body']</c>, <c>['send']</c>, <c>['discard']</c>, <c>['state', path]</c>,
/// <c>['shot', path]</c>. Later packages add verbs in their own files and reach the view through <see cref="Current"/>.
/// </summary>
internal static class ComposeDebugActions
{
    private static WeakReference<ComposeView>? _current;

    public static ComposeView? Current => _current is { } weak && weak.TryGetTarget(out var view) ? view : null;

    public static void SetCurrent(ComposeView view) => _current = new WeakReference<ComposeView>(view);

    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-compose", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) => _ = RunAsync(services, args.Parameter?.GetStrv(out var _) ?? []);
        application.AddAction(action);
    }

    private static async Task RunAsync(Func<IServiceProvider?> services, string[] arguments)
    {
        try
        {
            var command = Argument(arguments, 0);
            if (command == "new")
            {
                var provider = services() ?? throw new InvalidOperationException("The services are not ready.");
                var router = provider.GetRequiredService<ShortcutHost>().MainRouter ?? throw new InvalidOperationException("No shortcut router.");
                Log($"new handled={await router.DispatchActionAsync(KeyboardShortcutAction.NewMail)}");
                return;
            }
            var view = Current ?? throw new InvalidOperationException("No composer is shown.");
            var vm = view.ViewModel;
            switch (command)
            {
                case "to" or "cc" or "bcc": await view.DebugSetRecipientsAsync(command, Argument(arguments, 1)); break;
                case "subject": vm.Subject = Argument(arguments, 1); break;
                case "body":
                    await (view.Editor ?? throw new InvalidOperationException("No editor.")).RenderHtmlAsync(Argument(arguments, 1));
                    break;
                case "get-body": Log("body " + await (view.Editor ?? throw new InvalidOperationException("No editor.")).GetHtmlBodyAsync()); break;
                case "send": await view.SendAsync(); break;
                case "discard": await vm.DiscardCommand.ExecuteAsync(null); break;
                case "state":
                    DebugActions.WriteText(Argument(arguments, 1), Describe(view) + "\n");
                    break;
                case "shot": DebugActions.Capture(view.Root, Argument(arguments, 1)); break;
                default: throw new ArgumentException($"'{command}' is not a debug-compose command.");
            }
            Log("done " + command);
        }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-compose failed."); }
    }

    private static string Describe(ComposeView view)
    {
        var vm = view.ViewModel;
        static string Join(IEnumerable<Wino.Core.Domain.Entities.Shared.AccountContact> items) => string.Join(",", items.Select(static c => c.Address));
        var focus = view.Root.GetRoot() is Gtk.Window window && window.GetFocus() is { } widget ? widget.GetType().Name + ":" + widget.GetName() : "none";
        return $"to={Join(vm.ToItems)} cc={Join(vm.CCItems)} bcc={Join(vm.BCCItems)} subject={vm.Subject} canSend={vm.SendCommand.CanExecute(null)} " +
            $"isLocalDraft={vm.IsLocalDraft} busy={vm.IsDraftBusy} lastSaved={vm.LastSavedText} syncFailed={vm.IsDraftSyncFailed} " +
            $"importance={vm.SelectedMessageImportance} receipt={vm.IsReadReceiptRequested} ccbcc={vm.IsCCBCCVisible} focus={focus} editorDark={view.Editor?.CurrentState.IsDarkMode} viewDark={view.IsEditorDark} " +
            $"live={WinoLiveObjects.Instances(nameof(ComposeView)).Length}";
    }

    private static void Log(string line) => Serilog.Log.Information("compose-debug: {Line}", line);

    private static string Argument(string[] arguments, int index)
        => index < arguments.Length ? arguments[index] : throw new ArgumentException($"Argument {index} is missing.");
}
#endif
