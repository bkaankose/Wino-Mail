#if DEBUG
using Wino.Mail.Linux.Views.Onboarding;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// The server form: <c>debug-imap-fill "['&lt;lease or manifest path&gt;', '&lt;account key&gt;']"</c> types the lab
/// account into the widgets (field names are logged, never values); <c>debug-imap-type "[field, text]"</c> types one
/// field (<c>@lab:</c> values as in <see cref="AccountSetupDebugActions.LabValue"/>); <c>debug-imap-state 'path'</c>
/// writes the step, the visible parts, summaries and connection states; <c>debug-imap-step 'features'|'signin'|'servers'</c>;
/// <c>debug-imap-back ''</c> presses the page's Back;
/// <c>debug-imap-expand 'on'|'off'</c>; <c>debug-imap-primary ''</c>, <c>debug-imap-test ''</c> and
/// <c>debug-imap-save ''</c> press the buttons; <c>debug-imap-capability "['mail'|'calendar'|'contacts'|'tasks',
/// 'on'|'off'|'provider'|'local'|'expand'|'collapse']"</c> acts on a capability card as a click does.
/// </summary>
internal static class ImapSetupDebugActions
{
    public static void Register(Adw.Application application)
    {
        Add(application, "debug-imap-fill", "as", value =>
        {
            var args = value.GetStrv(out _);
            if (args.Length < 2) return;
            var set = Current().DebugFill(args[0], args[1]);
            Serilog.Log.Information("Debug action: server form filled {Fields}.", string.Join(",", set));
        });
        Add(application, "debug-imap-type", "as", value =>
        {
            var args = value.GetStrv(out _);
            if (args.Length < 2) return;
            Serilog.Log.Information("Debug action: server form field {Field} typed={Ok}.", args[0], Current().DebugType(args[0], AccountSetupDebugActions.LabValue(args[1])));
        });
        Add(application, "debug-imap-state", "s", value => DebugActions.WriteText(value.GetString(out _), Current().DebugDescribe()));
        Add(application, "debug-imap-step", "s", value =>
            Current().DebugViewModel.CurrentSetupStep = value.GetString(out _) switch
            {
                "servers" => ImapSetupStep.Servers,
                "features" => ImapSetupStep.Features,
                _ => ImapSetupStep.SignIn
            });
        Add(application, "debug-imap-back", "s", _ =>
        {
            var vm = Current().DebugViewModel;
            if (vm.BackCommand.CanExecute(null)) vm.BackCommand.Execute(null);
            else Serilog.Log.Information("Debug action: server form Back is disabled.");
        });
        Add(application, "debug-imap-expand", "s", value =>
        {
            var vm = Current().DebugViewModel;
            vm.IsIncomingExpanded = vm.IsOutgoingExpanded = vm.IsCalDavExpanded = vm.IsCardDavExpanded = value.GetString(out _) != "off";
        });
        Add(application, "debug-imap-primary", "s", _ => Run(Current().DebugViewModel.PrimaryActionCommand));
        Add(application, "debug-imap-test", "s", _ => Run(Current().DebugViewModel.TestConnectionsCommand));
        Add(application, "debug-imap-save", "s", _ => Run(Current().DebugViewModel.SaveCommand));
        Add(application, "debug-imap-capability", "as", value =>
        {
            var args = value.GetStrv(out _);
            if (args.Length < 2) return;
            Serilog.Log.Information("Debug action: capability {Card} {Action} applied={Ok}.", args[0], args[1], Current().DebugCapability(args[0], args[1]));
        });
    }

    private static ImapCalDavSettingsView Current() => ImapCalDavSettingsView.DebugCurrent ?? throw new InvalidOperationException("The server form is not open.");

    private static void Run(System.Windows.Input.ICommand command)
    {
        if (!command.CanExecute(null)) { Serilog.Log.Information("Debug action: command is disabled."); return; }
        if (command is CommunityToolkit.Mvvm.Input.IAsyncRelayCommand asyncCommand) Observe(asyncCommand.ExecuteAsync(null));
        else command.Execute(null);
    }

    private static void Add(Adw.Application application, string name, string type, Action<GLib.Variant> run)
    {
        var action = Gio.SimpleAction.New(name, GLib.VariantType.New(type));
        action.OnActivate += (_, args) =>
        {
            if (args.Parameter is not { } parameter) return;
            try { run(parameter); }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action {Action} failed.", name); }
        };
        application.AddAction(action);
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Server form debug action failed."); }
    }
}
#endif
