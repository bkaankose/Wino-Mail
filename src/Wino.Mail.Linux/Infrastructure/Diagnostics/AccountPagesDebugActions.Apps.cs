#if DEBUG
using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Settings.Accounts;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// App pages: <c>['app-state', path]</c>, <c>['app', 'on'|'off']</c>, <c>['source', 'server'|'device']</c> (as a card click),
/// <c>['server']</c> (the server row's button), <c>['calendar', name, 'sync'|'showas'|'reminder'|'color', value]</c>
/// (reminder in seconds or 'default'), <c>['folder', name, 'on'|'off']</c>.
/// </summary>
internal static partial class AccountPagesDebugActions
{
    private static IAccountAppPageDebug App() => IAccountAppPageDebug.Current ?? throw new InvalidOperationException("No account app page is open.");

    private static bool RunApp(string[] parts)
    {
        switch (parts)
        {
            case ["app-state", var path]:
                DebugActions.WriteText(path, App().DebugDescribe());
                return true;
            case ["app", var state]:
                App().DebugAppItem.IsOn = state == "on";
                return true;
            case ["source", var source]:
                Serilog.Log.Information("Debug action: source chosen={Ok}.", App().DebugChooseSource(source == "device" ? AccountCapabilityMode.Local : AccountCapabilityMode.Provider));
                return true;
            case ["server"]:
                App().DebugPressServer();
                return true;
            case ["calendar", var name, var field, var value]:
                Serilog.Log.Information("Debug action: calendar {Field} set={Ok}.", field,
                    (App() as AccountCalendarSettingsPage)?.DebugSet(name, field, value) ?? false);
                return true;
            case ["folder", var name, var state]:
                Serilog.Log.Information("Debug action: folder toggled={Ok}.", (App() as AccountMailSettingsPage)?.DebugToggleFolder(name, state == "on") ?? false);
                return true;
            default:
                return false;
        }
    }
}
#endif
