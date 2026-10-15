#if DEBUG
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain.Enums;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels.Diagnostics;
using Wino.Mail.ViewModels.Lifecycle;

namespace Wino.Mail.UIKit.Views.Contacts;

/// <summary>
/// Debug-only bridge commands that seed local data for the Contacts and To Do modes in the Simulator (no real account can be
/// added there) through the shared <see cref="ModeDebugSeed"/>: <c>seedpeople</c> grants People on the first account (or
/// the shared demo account) and fills a local address book; <c>seedtasks</c> grants To Do and fills two local lists with
/// tasks and steps. Both then reopen the mode so the singletons reload.
/// </summary>
internal sealed class ContactsDebugCommands(IServiceProvider services) : IApplicationLifecycleHook
{
    public Task ServicesReadyAsync()
    {
        UIKitDebugBridge.Register("seedpeople", _ => SeedAsync(ModeDebugSeed.SeedContactsAsync, WinoApplicationMode.Contacts));
        UIKitDebugBridge.Register("seedtasks", _ => SeedAsync(ModeDebugSeed.SeedTasksAsync, WinoApplicationMode.Tasks));
        return Task.CompletedTask;
    }

    public Task StoppingAsync(bool runtimeStarted) => Task.CompletedTask;

    private async Task<string> SeedAsync(Func<IServiceProvider, Task<string>> seed, WinoApplicationMode mode)
    {
        var result = await seed(services);
        var navigation = services.GetRequiredService<UIKitNavigationService>();
        navigation.ChangeApplicationMode(WinoApplicationMode.Mail);
        await Task.Delay(800);
        navigation.ChangeApplicationMode(mode);
        return result;
    }
}
#endif
