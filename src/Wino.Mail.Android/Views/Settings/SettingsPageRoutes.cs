using Wino.Core.Domain.Enums;
using Wino.Core.ViewModels;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Android.Views.Settings.Pages;
using Wino.Mail.ViewModels;

namespace Wino.Mail.Android.Views.Settings;

/// <summary>
/// The Settings routes (iOS SettingsPageRoutes, the Mac MacPageRegistry.Settings*.cs): every Settings page is a
/// <see cref="AndroidPageHost.Settings"/> page, shown by <see cref="SettingsHostFragment"/>. Settings is never a mode:
/// the account avatar and <c>ChangeApplicationMode(Settings)</c> open the <see cref="WinoPage.SettingsPage"/> root list.
/// Pages without an Android meaning (Companion, Backup and restore, Keyboard shortcuts, S/MIME) are not routed and the
/// shared <c>SettingsCatalog</c> does not list them (IPlatformCapabilities).
/// </summary>
public static partial class SettingsPageRoutes
{
    public static AndroidPageRegistry RegisterSettingsPages(this AndroidPageRegistry registry)
    {
        const AndroidPageHost host = AndroidPageHost.Settings;
        // The Windows home grid and the Settings mode open the root list.
        registry.Register<SettingsRootFragment, SettingOptionsPageViewModel>(WinoPage.SettingsPage, host);
        registry.Register<SettingsRootFragment, SettingOptionsPageViewModel>(WinoPage.SettingOptionsPage, host);

        // Windows pane order; account sub-pages follow Manage accounts.
        registry.Register<AppPreferencesPageFragment, AppPreferencesPageViewModel>(WinoPage.AppPreferencesPage, host,
            static (vm, mode, parameter) => vm.InitializeNavigationAsync(mode, parameter));
        RegisterPersonalizationPages(registry);
        RegisterGeneralAndMailPages(registry);
        RegisterCalendarContactsToDoPages(registry);
        RegisterAccountPages(registry);
        RegisterWinoAccountPages(registry);
        return registry;
    }

    // Each group lives in its own file (SettingsPageRoutes.<Group>.cs) beside its pages.
    static partial void RegisterPersonalizationPages(AndroidPageRegistry registry);
    static partial void RegisterGeneralAndMailPages(AndroidPageRegistry registry);
    static partial void RegisterCalendarContactsToDoPages(AndroidPageRegistry registry);
    static partial void RegisterAccountPages(AndroidPageRegistry registry);
    static partial void RegisterWinoAccountPages(AndroidPageRegistry registry);
}
