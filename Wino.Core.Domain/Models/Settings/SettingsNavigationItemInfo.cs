using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Settings;

public sealed class SettingsNavigationItemInfo(
    WinoPage? pageType,
    string title,
    string description,
    string glyph = "",
    bool isSeparator = false,
    string searchKeywords = "")
{
    public WinoPage? PageType { get; } = pageType;
    public string Title { get; } = title;
    public string Description { get; } = description;
    public string Glyph { get; } = glyph;
    public bool IsSeparator { get; } = isSeparator;
    public string SearchKeywords { get; } = searchKeywords;
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
                "\uE77B",
                searchKeywords: Translator.SettingsSearch_ManageAccounts_Keywords),
            new(WinoPage.WinoAccountManagementPage,
                Translator.WinoAccount_SettingsSection_Title,
                Translator.WinoAccount_SettingsSection_Description,
                "\uE77B",
                searchKeywords: string.Empty),
            new(null, Translator.SettingsOptions_GeneralSection, string.Empty, "\uE713", isSeparator: true),
            new(WinoPage.AppPreferencesPage,
                Translator.SettingsAppPreferences_Title,
                Translator.SettingsAppPreferences_Description,
                "\uE770",
                searchKeywords: Translator.SettingsSearch_AppPreferences_Keywords),
            new(WinoPage.KeyboardShortcutsPage,
                Translator.Settings_KeyboardShortcuts_Title,
                Translator.Settings_KeyboardShortcuts_Description,
                "\uE765",
                searchKeywords: Translator.SettingsSearch_KeyboardShortcuts_Keywords),
            new(WinoPage.PersonalizationPage,
                Translator.SettingsPersonalization_Title,
                Translator.SettingsPersonalization_Description,
                "\uE771",
                searchKeywords: Translator.SettingsSearch_Personalization_Keywords),
            new(WinoPage.AboutPage,
                Translator.SettingsAbout_Title,
                Translator.SettingsAbout_Description,
                "\uE946",
                searchKeywords: Translator.SettingsSearch_About_Keywords),
            new(null, Translator.SettingsOptions_MailSection, string.Empty, "\uE715", isSeparator: true),
            new(WinoPage.MessageListPage,
                Translator.SettingsMessageList_Title,
                Translator.SettingsMessageList_Description,
                "\uE8C4",
                searchKeywords: Translator.SettingsSearch_MessageList_Keywords),
            new(WinoPage.MailNotificationSettingsPage,
                Translator.SettingsMailNotifications_Title,
                Translator.SettingsMailNotifications_Description,
                "\uE7F4",
                searchKeywords: Translator.SettingsSearch_MailNotifications_Keywords),
            new(WinoPage.ReadComposePanePage,
                Translator.SettingsReadComposePane_Title,
                Translator.SettingsReadComposePane_Description,
                "\uE8BD",
                searchKeywords: Translator.SettingsSearch_ReadComposePane_Keywords),
            new(WinoPage.SignatureAndEncryptionPage,
                Translator.SettingsSignatureAndEncryption_Title,
                Translator.SettingsSignatureAndEncryption_Description,
                "\uE8D7",
                searchKeywords: Translator.SettingsSearch_SignatureAndEncryption_Keywords),
            new(WinoPage.EmailTemplatesPage,
                Translator.SettingsEmailTemplates_Title,
                Translator.SettingsEmailTemplates_Description,
                "\uE70F"),
            new(WinoPage.StoragePage,
                Translator.SettingsStorage_Title,
                Translator.SettingsStorage_Description,
                "\uE81C",
                searchKeywords: Translator.SettingsSearch_Storage_Keywords),
            new(null, Translator.SettingsOptions_CalendarSection, string.Empty, "\uE787", isSeparator: true),
            new(WinoPage.CalendarRenderingSettingsPage,
                Translator.CalendarSettings_Rendering_Title,
                Translator.CalendarSettings_Rendering_Description,
                "\uE787",
                searchKeywords: Translator.SettingsSearch_CalendarSettings_Keywords)
            ,
            new(WinoPage.CalendarNotificationSettingsPage,
                Translator.CalendarSettings_Notifications_Title,
                Translator.CalendarSettings_Notifications_Description,
                "\uE7F4",
                searchKeywords: Translator.SettingsSearch_CalendarSettings_Keywords),
            new(WinoPage.CalendarPreferenceSettingsPage,
                Translator.CalendarSettings_Preferences_Title,
                Translator.CalendarSettings_Preferences_Description,
                "\uE713",
                searchKeywords: Translator.SettingsSearch_CalendarSettings_Keywords)
        ];
    }

    public static IReadOnlyList<SettingsNavigationItemInfo> Search(string query, string manageAccountsDescription = "")
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var normalizedQuery = NormalizeSearchText(query);

        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return [];

        var queryTerms = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return GetNavigationItems(manageAccountsDescription)
            .Where(item => item.PageType.HasValue && !item.IsSeparator && item.PageType.Value != WinoPage.SettingOptionsPage)
            .Select(item => new
            {
                Item = item,
                Score = CalculateSearchScore(item, normalizedQuery, queryTerms)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Item.Title)
            .Select(x => x.Item)
            .ToList();
    }

    public static SettingsNavigationItemInfo GetInfo(WinoPage pageType, string manageAccountsDescription = "")
    {
        var rootPage = GetRootPage(pageType);
        return GetNavigationItems(manageAccountsDescription)
            .FirstOrDefault(item => item.PageType == rootPage)
            ?? GetNavigationItems(manageAccountsDescription).First(item => item.PageType == WinoPage.SettingOptionsPage);
    }

    public static string GetPageTitle(WinoPage pageType)
        => pageType switch
        {
            WinoPage.SettingOptionsPage => Translator.MenuSettings,
            WinoPage.ManageAccountsPage => Translator.SettingsManageAccountSettings_Title,
            WinoPage.AccountManagementPage => Translator.SettingsManageAccountSettings_Title,
            WinoPage.WinoAccountManagementPage => Translator.WinoAccount_SettingsSection_Title,
            WinoPage.PersonalizationPage => Translator.SettingsPersonalization_Title,
            WinoPage.AboutPage => Translator.SettingsAbout_Title,
            WinoPage.MessageListPage => Translator.SettingsMessageList_Title,
            WinoPage.MailNotificationSettingsPage => Translator.SettingsMailNotifications_Title,
            WinoPage.ReadComposePanePage => Translator.SettingsReadComposePane_Title,
            WinoPage.AppPreferencesPage => Translator.SettingsAppPreferences_Title,
            WinoPage.CalendarSettingsPage => Translator.CalendarSettings_Preferences_Title,
            WinoPage.CalendarRenderingSettingsPage => Translator.CalendarSettings_Rendering_Title,
            WinoPage.CalendarNotificationSettingsPage => Translator.CalendarSettings_Notifications_Title,
            WinoPage.CalendarPreferenceSettingsPage => Translator.CalendarSettings_Preferences_Title,
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
            WinoPage.FolderCustomizationPage => WinoPage.ManageAccountsPage,
            WinoPage.MailCategoryManagementPage => WinoPage.ManageAccountsPage,
            WinoPage.SignatureManagementPage => WinoPage.ManageAccountsPage,
            WinoPage.ImapCalDavSettingsPage => WinoPage.ManageAccountsPage,
            WinoPage.ProviderSelectionPage => WinoPage.ManageAccountsPage,
            WinoPage.SpecialImapCredentialsPage => WinoPage.ManageAccountsPage,
            WinoPage.AccountSetupProgressPage => WinoPage.ManageAccountsPage,
            WinoPage.CreateEmailTemplatePage => WinoPage.EmailTemplatesPage,
            WinoPage.CalendarSettingsPage => WinoPage.CalendarPreferenceSettingsPage,
            WinoPage.CalendarAccountSettingsPage => WinoPage.CalendarPreferenceSettingsPage,
            _ => pageType
        };

    private static int CalculateSearchScore(SettingsNavigationItemInfo item, string normalizedQuery, IReadOnlyList<string> queryTerms)
    {
        var title = NormalizeSearchText(item.Title);
        var description = NormalizeSearchText(item.Description);
        var keywords = NormalizeSearchText(item.SearchKeywords);
        var combinedText = string.Join(' ', new[] { title, description, keywords }.Where(text => !string.IsNullOrWhiteSpace(text)));

        if (!combinedText.Contains(normalizedQuery, StringComparison.Ordinal) &&
            !queryTerms.All(term => combinedText.Contains(term, StringComparison.Ordinal)))
        {
            return 0;
        }

        var score = 0;

        if (title.StartsWith(normalizedQuery, StringComparison.Ordinal))
            score += 500;
        else if (title.Contains(normalizedQuery, StringComparison.Ordinal))
            score += 360;

        if (keywords.Contains(normalizedQuery, StringComparison.Ordinal))
            score += 280;

        if (description.Contains(normalizedQuery, StringComparison.Ordinal))
            score += 180;

        foreach (var term in queryTerms)
        {
            if (title.Contains(term, StringComparison.Ordinal))
                score += 70;

            if (keywords.Contains(term, StringComparison.Ordinal))
                score += 50;

            if (description.Contains(term, StringComparison.Ordinal))
                score += 30;
        }

        return score;
    }

    private static string NormalizeSearchText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var sanitized = value
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
            .ToArray();

        return string.Join(' ', new string(sanitized).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
