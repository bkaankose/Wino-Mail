using Wino.Core.Domain.Enums;
using Wino.Core.ViewModels;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Android.Views.Settings.WinoAccount;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Android.Views.Settings;

/// <summary>
/// Wino Account and Wino Intelligence routes (iOS SettingsPageRoutes, the Mac MacPageRegistry.Settings*.cs). The pages
/// with an iOS InitializeAsync override use the registry's initializer overload; their release keeps OnNavigatedFrom.
/// </summary>
public static partial class SettingsPageRoutes
{
    static partial void RegisterWinoAccountPages(AndroidPageRegistry registry)
    {
        const AndroidPageHost host = AndroidPageHost.Settings;
        registry.Register<WinoAccountManagementPageFragment, WinoAccountManagementPageViewModel>(WinoPage.WinoAccountManagementPage, host,
            static (vm, mode, parameter) => WinoAccountManagementPageFragment.InitializeAsync(vm, mode, parameter));
        registry.Register<WinoIntelligencePageFragment, WinoAccountManagementPageViewModel>(WinoPage.WinoIntelligencePage, host,
            static (vm, mode, parameter) => WinoIntelligencePageFragment.InitializeAsync(vm, mode, parameter));
        registry.Register<WinoIntelligenceManagementPageFragment, WinoIntelligenceManagementPageViewModel>(WinoPage.WinoIntelligenceManagementPage, host,
            static (vm, mode, parameter) => vm.InitializeAsync(mode, parameter));
        registry.Register<IntelligenceCoveragePageFragment, IntelligenceCoveragePageViewModel>(WinoPage.IntelligenceCoveragePage, host,
            static (vm, mode, parameter) => IntelligenceCoveragePageFragment.InitializeAsync(vm, mode, parameter));
    }
}
