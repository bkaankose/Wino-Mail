using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Settings;

public sealed class SettingsNavigationItemInfo(
    WinoPage? pageType,
    string title,
    string description,
    string glyph = "",
    bool isSeparator = false)
{
    public WinoPage? PageType { get; } = pageType;
    public string Title { get; } = title;
    public string Description { get; } = description;
    public string Glyph { get; } = glyph;
    public bool IsSeparator { get; } = isSeparator;
}

public static class SettingsNavigationInfoProvider
{
    public static IReadOnlyList<SettingsNavigationItemInfo> GetNavigationItems(string manageAccountsDescription = "")
    {
        return
        [
            new(WinoPage.SettingOptionsPage,
                Translator.SettingsHome_Title,
                Translator.SettingsOptions_HeroDescription,
                "\uE80F"),
            new(WinoPage.ManageAccountsPage,
                Translator.SettingsManageAccountSettings_Title,
                manageAccountsDescription,
                "\uE77B"),
            new(null, Translator.SettingsOptions_GeneralSection, string.Empty, "\uE713", isSeparator: true),
            new(WinoPage.AppPreferencesPage,
                Translator.SettingsAppPreferences_Title,
                Translator.SettingsAppPreferences_Description,
                "\uE770"),
            new(WinoPage.LanguageTimePage,
                Translator.SettingsLanguageTime_Title,
                Translator.SettingsLanguageTime_Description,
                "\uE775"),
            new(WinoPage.PersonalizationPage,
                Translator.SettingsPersonalization_Title,
                Translator.SettingsPersonalization_Description,
                "\uE771"),
            new(WinoPage.AboutPage,
                Translator.SettingsAbout_Title,
                Translator.SettingsAbout_Description,
                "\uE946"),
            new(null, Translator.SettingsOptions_MailSection, string.Empty, "\uE715", isSeparator: true),
            new(WinoPage.KeyboardShortcutsPage,
                Translator.Settings_KeyboardShortcuts_Title,
                Translator.Settings_KeyboardShortcuts_Description,
                "\uE765"),
            new(WinoPage.MessageListPage,
                Translator.SettingsMessageList_Title,
                Translator.SettingsMessageList_Description,
                "\uE8C4"),
            new(WinoPage.ReadComposePanePage,
                Translator.SettingsReadComposePane_Title,
                Translator.SettingsReadComposePane_Description,
                "\uE8BD"),
            new(WinoPage.SignatureAndEncryptionPage,
                Translator.SettingsSignatureAndEncryption_Title,
                Translator.SettingsSignatureAndEncryption_Description,
                "\uE8D7"),
            new(WinoPage.StoragePage,
                Translator.SettingsStorage_Title,
                Translator.SettingsStorage_Description,
                "\uE81C"),
            new(null, Translator.SettingsOptions_CalendarSection, string.Empty, "\uE787", isSeparator: true),
            new(WinoPage.CalendarSettingsPage,
                Translator.SettingsCalendarSettings_Title,
                Translator.SettingsCalendarSettings_Description,
                "\uE787")
        ];
    }

    public static SettingsNavigationItemInfo GetInfo(WinoPage pageType, string manageAccountsDescription = "")
    {
        var rootPage = GetRootPage(pageType);
        return GetNavigationItems(manageAccountsDescription).First(item => item.PageType == rootPage);
    }

    public static string GetPageTitle(WinoPage pageType)
        => pageType switch
        {
            WinoPage.SettingOptionsPage => Translator.MenuSettings,
            WinoPage.ManageAccountsPage => Translator.SettingsManageAccountSettings_Title,
            WinoPage.AccountManagementPage => Translator.SettingsManageAccountSettings_Title,
            WinoPage.PersonalizationPage => Translator.SettingsPersonalization_Title,
            WinoPage.AboutPage => Translator.SettingsAbout_Title,
            WinoPage.MessageListPage => Translator.SettingsMessageList_Title,
            WinoPage.ReadComposePanePage => Translator.SettingsReadComposePane_Title,
            WinoPage.LanguageTimePage => Translator.SettingsLanguageTime_Title,
            WinoPage.AppPreferencesPage => Translator.SettingsAppPreferences_Title,
            WinoPage.CalendarSettingsPage => Translator.SettingsCalendarSettings_Title,
            WinoPage.SignatureAndEncryptionPage => Translator.SettingsSignatureAndEncryption_Title,
            WinoPage.KeyboardShortcutsPage => Translator.Settings_KeyboardShortcuts_Title,
            WinoPage.StoragePage => Translator.SettingsStorage_Title,
            WinoPage.EmailTemplatesPage => Translator.SettingsEmailTemplates_Title,
            WinoPage.CreateEmailTemplatePage => Translator.SettingsEmailTemplates_Title,
            _ => GetInfo(pageType).Title
        };

    public static WinoPage GetRootPage(WinoPage pageType)
        => pageType switch
        {
            WinoPage.AccountManagementPage => WinoPage.ManageAccountsPage,
            WinoPage.AccountDetailsPage => WinoPage.ManageAccountsPage,
            WinoPage.MergedAccountDetailsPage => WinoPage.ManageAccountsPage,
            WinoPage.AliasManagementPage => WinoPage.ManageAccountsPage,
            WinoPage.SignatureManagementPage => WinoPage.ManageAccountsPage,
            WinoPage.ImapCalDavSettingsPage => WinoPage.ManageAccountsPage,
            WinoPage.EmailTemplatesPage => WinoPage.ManageAccountsPage,
            WinoPage.CreateEmailTemplatePage => WinoPage.ManageAccountsPage,
            WinoPage.CalendarAccountSettingsPage => WinoPage.CalendarSettingsPage,
            _ => pageType
        };
}
