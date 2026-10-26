using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Mail.ViewModels.Navigation;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Prepares Settings once services are ready: the sheet's Done action. The catalog's platform and entitlement rules come
/// from the shared SettingsCatalogInitializer. The routes are added by the idiom registries
/// (<see cref="SettingsPageRoutes.RegisterSettingsPages"/>).
/// </summary>
public sealed class SettingsLifecycleHook(IServiceProvider services) : IApplicationLifecycleHook
{
    public Task ServicesReadyAsync()
    {
        var navigation = services.GetRequiredService<UIKitNavigationService>();
        SettingsSheetChrome.Close = () => ((INavigationHost)navigation).CloseSettingsAsync();
#if DEBUG
        SettingsDebug.Register(services);
#endif
        return Task.CompletedTask;
    }

    public Task StoppingAsync(bool runtimeStarted) => Task.CompletedTask;
}
