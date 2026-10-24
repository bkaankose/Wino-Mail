using Microsoft.Extensions.DependencyInjection;
using Wino.Mail.UIKit.Views.Settings;
using Wino.Mail.ViewModels.Lifecycle;

namespace Wino.Mail.UIKit;

/// <summary>
/// Settings sheet pages, Wino Account sheets and dialogs (the Mac MacViewModelRegistration.Settings*.cs). ViewModels
/// come from the shared RegisterWinoViewModels list; the idiom registries add the routes through
/// <see cref="SettingsPageRoutes.RegisterSettingsPages"/>.
/// <see cref="RegisterUIKitViews"/> calls <c>RegisterSettingsViews(services)</c>.
/// </summary>
public static partial class UIKitViewRegistration
{
    static partial void RegisterSettingsViews(IServiceCollection services);

    static partial void RegisterSettingsViews(IServiceCollection services)
    {
        services.AddSingleton<IApplicationLifecycleHook, SettingsLifecycleHook>();
        services.AddTransient<SettingsRootViewController>();
        services.AddTransient<AboutPageViewController>();
        services.AddTransient<AccountDetailsPageViewController>();
        services.AddTransient<AccountUnreadBadgePageViewController>();
        services.AddTransient<AliasManagementPageViewController>();
        services.AddTransient<AppPreferencesPageViewController>();
        services.AddTransient<ApplicationThemeEditorPageViewController>();
        services.AddTransient<ApplicationThemeGalleryPageViewController>();
        services.AddTransient<CalendarAccountSettingsPageViewController>();
        services.AddTransient<CalendarPreferenceSettingsPageViewController>();
        services.AddTransient<CalendarRenderingSettingsPageViewController>();
        services.AddTransient<ContactsPreferenceSettingsPageViewController>();
        services.AddTransient<CreateEmailTemplatePageViewController>();
        services.AddTransient<EmailTemplatesPageViewController>();
        services.AddTransient<FolderCustomizationPageViewController>();
        services.AddTransient<IntelligenceCoveragePageViewController>();
        services.AddTransient<KeyboardShortcutsPageViewController>();
        services.AddTransient<MailCategoryManagementPageViewController>();
        services.AddTransient<MailFilterEditorPageViewController>();
        services.AddTransient<MailFiltersPageViewController>();
        services.AddTransient<MailPreferencesPageViewController>();
        services.AddTransient<ManageAccountsPageViewController>();
        services.AddTransient<MergedAccountDetailsPageViewController>();
        services.AddTransient<MessageListPageViewController>();
        services.AddTransient<NotificationSettingsPageViewController>();
        services.AddTransient<PersonalizationPageViewController>();
        services.AddTransient<ReadComposePanePageViewController>();
        services.AddTransient<ReaderActionsPageViewController>();
        services.AddTransient<SignatureManagementPageViewController>();
        services.AddTransient<StoragePageViewController>();
        services.AddTransient<ToDoPreferenceSettingsPageViewController>();
        services.AddTransient<UnreadBadgeSettingsPageViewController>();
        services.AddTransient<WinoAccountManagementPageViewController>();
        services.AddTransient<WinoIntelligenceManagementPageViewController>();
        services.AddTransient<WinoIntelligencePageViewController>();
    }
}
