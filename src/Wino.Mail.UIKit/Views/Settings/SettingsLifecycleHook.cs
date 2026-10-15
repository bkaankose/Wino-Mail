using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels.Lifecycle;
using Wino.Mail.ViewModels.Navigation;
using Wino.Core.Domain.Models.Settings;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Prepares Settings once services are ready (the Mac SettingsWindowPresenter constructor): the catalog's platform and
/// entitlement rules and the sheet's Done action. The routes are added by the idiom registries
/// (<see cref="SettingsPageRoutes.RegisterSettingsPages"/>).
/// </summary>
public sealed class SettingsLifecycleHook(IServiceProvider services) : IApplicationLifecycleHook
{
    public Task ServicesReadyAsync()
    {
        // The shared list hides what this device lacks (S/MIME, the Companion, Backup and restore, and Keyboard shortcuts on
        // iPhone) through IPlatformCapabilities.
        var entitlement = services.GetService<IWinoAccountIntelligenceSnapshotService>();
        SettingsCatalog.Configure(services.GetRequiredService<IPlatformCapabilities>(),
            () => entitlement?.CurrentEntitlement?.CanAccessSurfaces ?? false);

        var navigation = services.GetRequiredService<UIKitNavigationService>();
        SettingsSheetChrome.Close = () => ((INavigationHost)navigation).CloseSettingsAsync();
#if DEBUG
        SettingsDebug.Register(services);
#endif
        return Task.CompletedTask;
    }

    public Task StoppingAsync(bool runtimeStarted) => Task.CompletedTask;
}
