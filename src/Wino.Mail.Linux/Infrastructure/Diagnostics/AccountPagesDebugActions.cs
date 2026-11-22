#if DEBUG
using System.Globalization;
using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Settings.Accounts;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// The account pages of Settings, <c>debug-account-page</c> (<c>as</c>). Hub: <c>['state', path]</c>,
/// <c>['identity', 'open'|'close']</c>, <c>['name', text]</c>, <c>['color', index|'reset']</c>,
/// <c>['open', 'mail'|'calendar'|'people'|'todo'|'signin'|'troubleshooting']</c>, <c>['hub-app', app, 'on'|'off']</c>, <c>['remove']</c>.
/// Every verb works on the page that is shown.
/// </summary>
internal static partial class AccountPagesDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-account-page", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) =>
        {
            var parts = args.Parameter?.GetStrv(out _) ?? [];
            try
            {
                if (!RunHub(parts) && !RunApp(parts) && !RunSignIn(parts))
                    throw new ArgumentException($"Unknown debug-account-page operation '{string.Join(' ', parts)}'.");
            }
            catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-account-page failed."); }
        };
        application.AddAction(action);
    }

    private static AccountHubPage Hub() => AccountHubPage.DebugCurrent ?? throw new InvalidOperationException("The account page is not open.");

    private static bool RunHub(string[] parts)
    {
        switch (parts)
        {
            case ["state", var path]:
                DebugActions.WriteText(path, Hub().DebugDescribe());
                return true;
            case ["identity", var state]:
            {
                var vm = Hub().DebugViewModel;
                if (vm.IsIdentityEditorOpen != (state == "open")) vm.ToggleIdentityEditorCommand.Execute(null);
                return true;
            }
            case ["name", var text]:
                Hub().DebugViewModel.AccountName = text;
                return true;
            case ["color", "reset"]:
                Hub().DebugViewModel.ResetColorCommand.Execute(null);
                return true;
            case ["color", var index]:
                Serilog.Log.Information("Debug action: hub colour picked={Ok}.", Hub().DebugPickColor(int.Parse(index, CultureInfo.InvariantCulture)));
                return true;
            case ["open", var target]:
            {
                var vm = Hub().DebugViewModel;
                switch (target)
                {
                    case "signin": Execute(vm.OpenSignInCommand, null); break;
                    case "troubleshooting": Execute(vm.OpenTroubleshootingCommand, null); break;
                    default: Execute(vm.OpenAppCommand, App(target)); break;
                }
                return true;
            }
            case ["hub-app", var app, var state]:
            {
                var item = Hub().DebugViewModel.Apps.First(candidate => candidate.App == App(app));
                item.IsOn = state == "on";
                return true;
            }
            case ["remove"]:
                Execute(Hub().DebugViewModel.RemoveAccountCommand, null);
                return true;
            default:
                return false;
        }
    }

    internal static WinoApplicationMode App(string name) => name switch
    {
        "mail" => WinoApplicationMode.Mail,
        "calendar" => WinoApplicationMode.Calendar,
        "people" or "contacts" => WinoApplicationMode.Contacts,
        "todo" or "tasks" => WinoApplicationMode.Tasks,
        _ => throw new ArgumentException($"Unknown app '{name}'.")
    };

    internal static void Execute(System.Windows.Input.ICommand command, object? parameter)
    {
        if (command.CanExecute(parameter)) command.Execute(parameter);
        else Serilog.Log.Information("Debug action: command is disabled.");
    }
}
#endif
