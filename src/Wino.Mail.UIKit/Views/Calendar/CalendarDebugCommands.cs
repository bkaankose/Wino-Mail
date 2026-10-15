#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels.Lifecycle;

namespace Wino.Mail.UIKit.Views.Calendar;

/// <summary>
/// Debug-only Simulator aid (no real calendar account can be added there): <c>seedcalendar [ADDRESS]</c> runs the shared
/// <see cref="Wino.Mail.ViewModels.Diagnostics.CalendarDebugSeed"/> (demo calendars and events on the first account, or the
/// one whose address contains ADDRESS), then opens Calendar. Run it before the calendar shell first loads its calendars,
/// or restart the app afterwards. <c>calcompose-close</c> closes the composer
/// sheet as Cancel would.
/// </summary>
internal sealed class CalendarDebugCommands(IServiceProvider services) : IApplicationLifecycleHook
{
    public Task ServicesReadyAsync()
    {
        UIKitDebugBridge.Register("seedcalendar", args => SeedAsync(args.Length > 0 ? args[0] : null));
        UIKitDebugBridge.Register("calcompose-close", async _ =>
        {
            if (CalendarComposerSheet.Current is not { } sheet) return "no composer sheet";
            await sheet.CloseAsync();
            return "ok";
        });
        return Task.CompletedTask;
    }

    public Task StoppingAsync(bool runtimeStarted) => Task.CompletedTask;

    /// <summary>The shared demo calendars (<see cref="Wino.Mail.ViewModels.Diagnostics.CalendarDebugSeed"/>), then Calendar opens.</summary>
    private async Task<string> SeedAsync(string? address)
    {
        var result = await Wino.Mail.ViewModels.Diagnostics.CalendarDebugSeed.SeedAsync(services, address);
        services.GetRequiredService<UIKitNavigationService>().ChangeApplicationMode(WinoApplicationMode.Calendar);
        return result;
    }
}
#endif
