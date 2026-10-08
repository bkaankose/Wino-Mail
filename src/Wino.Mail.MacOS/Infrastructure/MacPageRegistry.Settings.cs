using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Views.Settings;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>Settings window routes. Owned by the Settings feature.</summary>
public sealed partial class MacPageRegistry
{
    private static readonly WinoPage[] DeferredSettingsPages =
    [
        WinoPage.CompanionSettingsPage,
        WinoPage.SignatureManagementPage, WinoPage.EmailTemplatesPage, WinoPage.CreateEmailTemplatePage,
        WinoPage.MergedAccountDetailsPage, WinoPage.FolderCustomizationPage,
        WinoPage.MailFiltersPage, WinoPage.MailFilterEditorPage, WinoPage.AccountUnreadBadgePage,
        WinoPage.ApplicationThemeGalleryPage, WinoPage.ApplicationThemeEditorPage
    ];

    // Pages being ported in their own partial files (MacPageRegistry.Settings.<Feature>.cs).
    partial void RegisterCalendarSettingsPages();
    partial void RegisterCategorySettingsPages();
    partial void RegisterSmimeSettingsPages();
    partial void RegisterBackupSettingsPages();
    partial void RegisterWinoAccountPages();

    partial void RegisterSettingsPages()
    {
        // The Windows home grid and shell mode open the Settings window at its default page.
        Register<SettingsPlaceholderViewController>(WinoPage.SettingOptionsPage, MacPageHost.SettingsWindow);
        Register<SettingsPlaceholderViewController>(WinoPage.SettingsPage, MacPageHost.SettingsWindow);

        Register<AppPreferencesPageViewController>(WinoPage.AppPreferencesPage, MacPageHost.SettingsWindow);
        Register<MessageListPageViewController>(WinoPage.MessageListPage, MacPageHost.SettingsWindow);
        Register<AboutPageViewController>(WinoPage.AboutPage, MacPageHost.SettingsWindow);
        Register<PersonalizationPageViewController>(WinoPage.PersonalizationPage, MacPageHost.SettingsWindow);
        Register<ManageAccountsPageViewController>(WinoPage.ManageAccountsPage, MacPageHost.SettingsWindow);
        Register<ManageAccountsPageViewController>(WinoPage.AccountManagementPage, MacPageHost.SettingsWindow);
        Register<AccountDetailsPageViewController>(WinoPage.AccountDetailsPage, MacPageHost.SettingsWindow);
        Register<MailPreferencesPageViewController>(WinoPage.MailPreferencesPage, MacPageHost.SettingsWindow);
        Register<ReadComposePanePageViewController>(WinoPage.ReadComposePanePage, MacPageHost.SettingsWindow);
        Register<NotificationSettingsPageViewController>(WinoPage.NotificationSettingsPage, MacPageHost.SettingsWindow);
        Register<UnreadBadgeSettingsPageViewController>(WinoPage.UnreadBadgeSettingsPage, MacPageHost.SettingsWindow);
        Register<ContactsPreferenceSettingsPageViewController>(WinoPage.ContactsPreferenceSettingsPage, MacPageHost.SettingsWindow);
        Register<ToDoPreferenceSettingsPageViewController>(WinoPage.ToDoPreferenceSettingsPage, MacPageHost.SettingsWindow);
        Register<StoragePageViewController>(WinoPage.StoragePage, MacPageHost.SettingsWindow);
        Register<KeyboardShortcutsPageViewController>(WinoPage.KeyboardShortcutsPage, MacPageHost.SettingsWindow);
        Register<CalendarPreferenceSettingsPageViewController>(WinoPage.CalendarPreferenceSettingsPage, MacPageHost.SettingsWindow);
        Register<CalendarPreferenceSettingsPageViewController>(WinoPage.CalendarSettingsPage, MacPageHost.SettingsWindow);
        Register<CalendarRenderingSettingsPageViewController>(WinoPage.CalendarRenderingSettingsPage, MacPageHost.SettingsWindow);
        Register<AliasManagementPageViewController>(WinoPage.AliasManagementPage, MacPageHost.SettingsWindow);
        Register<WinoIntelligencePageViewController>(WinoPage.WinoIntelligencePage, MacPageHost.SettingsWindow);
        Register<WinoIntelligenceManagementPageViewController>(WinoPage.WinoIntelligenceManagementPage, MacPageHost.SettingsWindow);
        Register<IntelligenceCoveragePageViewController>(WinoPage.IntelligenceCoveragePage, MacPageHost.SettingsWindow);

        RegisterCalendarSettingsPages();
        RegisterCategorySettingsPages();
        RegisterSmimeSettingsPages();
        RegisterBackupSettingsPages();
        RegisterWinoAccountPages();

        // Pages without a native implementation yet show a clear placeholder inside the window.
        foreach (var page in DeferredSettingsPages)
            Register<SettingsPlaceholderViewController>(page, MacPageHost.SettingsWindow);
    }
}
