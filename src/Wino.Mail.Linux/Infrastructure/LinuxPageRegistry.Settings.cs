using Wino.Core.Domain.Enums;
using Wino.Mail.Linux.Views.Settings;

namespace Wino.Mail.Linux.Infrastructure;

internal sealed partial class LinuxPageRegistry
{
    private static readonly WinoPage[] SettingsPages =
    [
        WinoPage.SettingOptionsPage,
        WinoPage.SettingsPage,
        WinoPage.ManageAccountsPage,
        WinoPage.AccountManagementPage,
        WinoPage.AccountDetailsPage,
        WinoPage.MergedAccountDetailsPage,
        WinoPage.AliasManagementPage,
        WinoPage.SignatureManagementPage,
        WinoPage.FolderCustomizationPage,
        WinoPage.MailCategoryManagementPage,
        WinoPage.MailFiltersPage,
        WinoPage.MailFilterEditorPage,
        WinoPage.AccountUnreadBadgePage,
        WinoPage.WinoAccountManagementPage,
        WinoPage.WinoIntelligencePage,
        WinoPage.WinoIntelligenceManagementPage,
        WinoPage.IntelligenceCoveragePage,
        WinoPage.MailClassificationPage,
        WinoPage.AppPreferencesPage,
        WinoPage.NotificationSettingsPage,
        WinoPage.PersonalizationPage,
        WinoPage.ApplicationThemeGalleryPage,
        WinoPage.ApplicationThemeEditorPage,
        WinoPage.AppLockPage,
        WinoPage.KeyboardShortcutsPage,
        WinoPage.BackupRestorePage,
        WinoPage.AboutPage,
        WinoPage.MailPreferencesPage,
        WinoPage.MessageListPage,
        WinoPage.UnreadBadgeSettingsPage,
        WinoPage.ReadComposePanePage,
        WinoPage.ReaderActionsPage,
        WinoPage.PrivacySecurityPage,
        WinoPage.RemoteContentRulesPage,
        WinoPage.SignatureAndEncryptionPage,
        WinoPage.EmailTemplatesPage,
        WinoPage.CreateEmailTemplatePage,
        WinoPage.StoragePage,
        WinoPage.CalendarSettingsPage,
        WinoPage.CalendarPreferenceSettingsPage,
        WinoPage.CalendarRenderingSettingsPage,
        WinoPage.CalendarAccountSettingsPage,
        WinoPage.ContactsPreferenceSettingsPage,
        WinoPage.ToDoPreferenceSettingsPage,
    ];

    // Runs first among the settings hooks, so later settings hooks overwrite the placeholder; a page another area
    // registered earlier for another host is kept.
    partial void RegisterSettingsPages()
    {
        foreach (var page in SettingsPages)
        {
            if (!_pages.ContainsKey(page)) Register<SettingsPlaceholderView>(page, LinuxPageHost.Settings);
        }
    }
}
