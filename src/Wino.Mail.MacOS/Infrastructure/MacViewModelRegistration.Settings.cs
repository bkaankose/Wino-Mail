using Microsoft.Extensions.DependencyInjection;
using Wino.Calendar.ViewModels;
using Wino.Core.ViewModels;
using Wino.Mail.MacOS.Views.Settings;
using Wino.Mail.ViewModels;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Settings window views and ViewModels. Owned by the Settings feature.</summary>
public static partial class MacViewModelRegistration
{
    static partial void RegisterThemeSettingsViews(IServiceCollection services);

    static partial void RegisterSettingsViews(IServiceCollection services)
    {
        RegisterThemeSettingsViews(services);
        services.AddSingleton<SettingsWindowPresenter>();
        services.AddSingleton<ISettingsWindowPresenter>(provider => provider.GetRequiredService<SettingsWindowPresenter>());

        // ViewModels come from the shared RegisterWinoViewModels list.
        services.AddTransient<AboutPageViewController>();
        services.AddTransient<AppPreferencesPageViewController>();
        services.AddTransient<MessageListPageViewController>();
        services.AddTransient<PersonalizationPageViewController>();
        services.AddTransient<ManageAccountsPageViewController>();
        services.AddTransient<AccountDetailsPageViewController>();
        services.AddTransient<MailPreferencesPageViewController>();
        services.AddTransient<ReadComposePanePageViewController>();
        services.AddTransient<NotificationSettingsPageViewController>();
        services.AddTransient<UnreadBadgeSettingsPageViewController>();
        services.AddTransient<ContactsPreferenceSettingsPageViewController>();
        services.AddTransient<ToDoPreferenceSettingsPageViewController>();
        services.AddTransient<StoragePageViewController>();
        services.AddTransient<KeyboardShortcutsPageViewController>();
        services.AddTransient<CalendarPreferenceSettingsPageViewController>();
        services.AddTransient<CalendarRenderingSettingsPageViewController>();
        services.AddTransient<AliasManagementPageViewController>();
        services.AddTransient<WinoIntelligencePageViewController>();
        services.AddTransient<WinoIntelligenceManagementPageViewController>();
        services.AddTransient<IntelligenceCoveragePageViewController>();
#if DEBUG
        WinoIntelligenceDebug.Register();
        Views.Account.WinoAccountSheetsDebug.Register();
#endif
    }
}
