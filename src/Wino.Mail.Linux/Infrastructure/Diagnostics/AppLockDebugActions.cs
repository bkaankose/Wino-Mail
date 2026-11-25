#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Interfaces;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-applock</c> <c>as</c>: set pin (turns the lock on with that PIN, no verification), lock (LockSession),
/// unlock pin (types it into the lock screen and presses Unlock), state, readonly on|off, off (turns the lock off without asking),
/// verify (shows the verification dialog; the result is logged when it closes). One log line
/// <c>Debug action debug-applock &lt;verb&gt;: &lt;result&gt;</c>; a last argument ending in .txt also receives it.
/// </summary>
internal static class AppLockDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-applock", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) => _ = RunAsync(services, args.Parameter?.GetStrv(out _) ?? []);
        application.AddAction(action);
    }

    private static async Task RunAsync(Func<IServiceProvider?> services, string[] arguments)
    {
        var verb = arguments.Length > 0 ? arguments[0] : string.Empty;
        string? path = arguments.Length > 1 && arguments[^1].EndsWith(".txt", StringComparison.Ordinal) ? arguments[^1] : null;
        var argument = arguments.Length > 1 && (path is null || arguments.Length > 2) ? arguments[1] : string.Empty;
        string result;
        try
        {
            var provider = services() ?? throw new InvalidOperationException("no services");
            var appLock = provider.GetRequiredService<IAppLockService>();
            var coordinator = provider.GetRequiredService<AppLockCoordinator>();
            switch (verb)
            {
                case "set":
                    result = appLock.EnableWithPin(argument) ? "enabled" : "refused";
                    break;
                case "lock":
                    appLock.LockSession();
                    result = "locked=" + appLock.IsLocked;
                    break;
                case "unlock":
                    if (coordinator.Screen is not { } screen) { result = "no lock screen"; break; }
                    await screen.DebugUnlockAsync(argument);
                    result = $"locked={appLock.IsLocked} error='{coordinator.Screen?.ErrorText}' canUnlock={coordinator.Screen?.CanUnlock} throttled={coordinator.Screen?.IsThrottled}";
                    break;
                case "readonly":
                    result = "applied=" + appLock.SetNotificationsReadOnly(argument == "on") + " readOnly=" + appLock.AreNotificationsReadOnly;
                    break;
                case "off":
                    appLock.Disable();
                    result = "enabled=" + appLock.IsEnabled;
                    break;
                case "verify":
                    var verified = await provider.GetRequiredService<GtkDialogService>().ShowAppLockVerificationDialogAsync();
                    result = "verified=" + verified;
                    break;
                default:
                    result = State(provider, appLock, coordinator);
                    break;
            }
        }
        catch (Exception exception)
        {
            result = "error: " + exception.Message;
        }
        Serilog.Log.Information("Debug action debug-applock {Verb}: {Result}", verb, result);
        if (path is not null) DebugActions.WriteText(path, result);
    }

    private static string State(IServiceProvider provider, IAppLockService appLock, AppLockCoordinator coordinator)
    {
        var window = provider.GetService<GtkNavigationService>()?.MainWindow;
        var windows = provider.GetService<WindowManager>()?.Describe() ?? [];
        var router = provider.GetService<ShortcutHost>()?.MainRouter;
        var disabled = Gio.Application.GetDefault() is { } application
            ? string.Join(",", application.ListActions().Where(name => !name.StartsWith("debug-", StringComparison.Ordinal)
                && application.LookupAction(name) is Gio.SimpleAction action && !action.GetEnabled()).Order(StringComparer.Ordinal))
            : string.Empty;
        return $"enabled={appLock.IsEnabled} locked={appLock.IsLocked} method={appLock.Method} readOnly={appLock.AreNotificationsReadOnly} "
            + $"screen={coordinator.IsLocked} windowLocked={window?.IsLocked} routerSuspended={router?.IsSuspended} disabledActions=[{disabled}] "
            + $"windows=[{string.Join(" ; ", windows)}] error='{coordinator.Screen?.ErrorText}'";
    }
}
#endif
