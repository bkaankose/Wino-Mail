#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.Linux.Infrastructure.Updates;

namespace Wino.Mail.Linux.Infrastructure.Diagnostics;

/// <summary>
/// <c>debug-update-check</c> (<c>as</c>): <c>['check']</c> runs the manual check of the menu item; <c>['auto']</c> runs
/// the automatic check now, ignoring the 24-hour rule; <c>['state', path]</c> writes the feed, the switch and the last
/// check. Point the app at a local feed with <c>WINO_DEBUG_DISTRIBUTION=&lt;file&gt;</c>, <c>WINO_DEBUG_UPDATE_FEED=&lt;url or
/// file&gt;</c> and <c>WINO_DEBUG_UPDATE_DOWNLOADS=&lt;folder&gt;</c>; the installer is then not launched.
/// </summary>
internal static class UpdateDebugActions
{
    public static void Register(Adw.Application application, Func<IServiceProvider?> services)
    {
        var action = Gio.SimpleAction.New("debug-update-check", GLib.VariantType.New("as"));
        action.OnActivate += (sender, args) => Observe(RunAsync(services, args.Parameter?.GetStrv(out _) ?? []));
        application.AddAction(action);
    }

    private static async Task RunAsync(Func<IServiceProvider?> services, string[] parts)
    {
        var provider = services() ?? throw new InvalidOperationException("The app has not started.");
        var updates = provider.GetRequiredService<LinuxUpdateService>();
        switch (parts)
        {
            case ["check"]:
                await provider.GetRequiredService<LinuxUpdatePresenter>().CheckNowAsync();
                break;
            case ["auto"]:
                await updates.DebugRunAutomaticAsync();
                break;
            case ["state", var path]:
                DebugActions.WriteText(path, updates.DebugDescribe());
                break;
            default:
                Serilog.Log.Warning("debug-update-check: unknown arguments {Arguments}.", string.Join(' ', parts));
                break;
        }
    }

    private static async void Observe(Task task)
    {
        try { await task; }
        catch (Exception exception) { Serilog.Log.Error(exception, "Debug action debug-update-check failed."); }
    }
}
#endif
