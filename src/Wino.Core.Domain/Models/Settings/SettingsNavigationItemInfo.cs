using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Core.Domain.Models.Settings;

public sealed class SettingsNavigationItemInfo(
    WinoPage? pageType,
    string title,
    string description,
    string glyph = "",
    bool isSeparator = false,
    string searchKeywords = "",
    SettingsNavigationRoute navigationRoute = null,
    string iconPathData = "")
{
    public WinoPage? PageType { get; } = pageType;
    public string Title { get; } = title;
    public string Description { get; } = description;
    public string Glyph { get; } = glyph;
    public bool IsSeparator { get; } = isSeparator;
    public string SearchKeywords { get; } = searchKeywords;
    public SettingsNavigationRoute NavigationRoute { get; } = navigationRoute;
    public string IconPathData { get; } = iconPathData;
}

/// <summary>
/// One row of the settings pane: either a page at the root, or a collapsible mode group.
/// </summary>
public sealed class SettingsNavigationPaneNode
{
    public SettingsNavigationPaneNode(SettingsNavigationItemInfo item)
    {
        Item = item;
        Title = item.Title;
        Glyph = item.Glyph;
    }

    public SettingsNavigationPaneNode(string title, string glyph, IReadOnlyList<SettingsNavigationItemInfo> children)
    {
        Title = title;
        Glyph = glyph;
        Children = children;
    }

    /// <summary>The page this row navigates to. Null for a group.</summary>
    public SettingsNavigationItemInfo Item { get; }

    public string Title { get; }

    public string Glyph { get; }

    /// <summary>The pages inside this group. Null for a root-level page.</summary>
    public IReadOnlyList<SettingsNavigationItemInfo> Children { get; }

    public bool IsGroup => Children is not null;
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
            new(WinoPage.WinoIntelligencePage,
                Translator.WinoIntelligence_SettingsTitle,
                Translator.WinoIntelligence_SettingsDescription,
                "\uE945",
                searchKeywords: Translator.SettingsSearch_WinoIntelligence_Keywords),
            new(null, Translator.SettingsOptions_GeneralSection, string.Empty, "\uE713", isSeparator: true),
            new(WinoPage.AppPreferencesPage,
                Translator.SettingsGeneral_Title,
                Translator.SettingsGeneral_Description,
                "\uE770",
                searchKeywords: Translator.SettingsSearch_General_Keywords),
            new(WinoPage.PersonalizationPage,
                Translator.SettingsPersonalization_Title,
                Translator.SettingsPersonalization_Description,
                "\uE771",
                searchKeywords: Translator.SettingsSearch_Personalization_Keywords),
            new(WinoPage.KeyboardShortcutsPage,
                Translator.Settings_KeyboardShortcuts_Title,
                Translator.Settings_KeyboardShortcuts_Description,
                "\uE765",
                searchKeywords: Translator.SettingsSearch_KeyboardShortcuts_Keywords),
            new(WinoPage.BackupRestorePage,
                Translator.SettingsBackupRestore_Title,
                Translator.SettingsBackupRestore_Description,
                "\uE8F7",
                searchKeywords: Translator.SettingsSearch_BackupRestore_Keywords),
            new(WinoPage.AboutPage,
                Translator.SettingsAbout_Title,
                Translator.SettingsAbout_Description,
                "\uE946",
                searchKeywords: Translator.SettingsSearch_About_Keywords),
            new(null, Translator.SettingsOptions_MailSection, string.Empty, "\uE715", isSeparator: true),
            new(WinoPage.MailPreferencesPage,
                Translator.SettingsMailPreferences_Title,
                Translator.SettingsMailPreferences_Description,
                "\uE713",
                searchKeywords: Translator.SettingsSearch_MailPreferences_Keywords),
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
            new(WinoPage.UnreadBadgeSettingsPage,
                Translator.UnreadBadges_Title,
                Translator.UnreadBadges_Description,
                "",
                searchKeywords: Translator.SettingsSearch_UnreadBadges_Keywords),
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
            new(WinoPage.CalendarPreferenceSettingsPage,
                Translator.CalendarSettings_Preferences_Title,
                Translator.CalendarSettings_Preferences_Description,
                "\uE713",
                searchKeywords: Translator.SettingsSearch_CalendarSettings_Keywords),
            new(WinoPage.CalendarRenderingSettingsPage,
                Translator.CalendarSettings_Rendering_Title,
                Translator.CalendarSettings_Rendering_Description,
                searchKeywords: Translator.SettingsSearch_CalendarSettings_Keywords,
                iconPathData: CalendarRenderingIconPathData),
            new(WinoPage.CalendarNotificationSettingsPage,
                Translator.CalendarSettings_Notifications_Title,
                Translator.CalendarSettings_Notifications_Description,
                "\uE7F4",
                searchKeywords: Translator.SettingsSearch_CalendarSettings_Keywords),
            new(null, Translator.SettingsOptions_PeopleSection, string.Empty, "\uE77B", isSeparator: true),
            new(WinoPage.ContactsPreferenceSettingsPage,
                Translator.PeopleSettings_Title,
                Translator.PeopleSettings_Description,
                "\uE713",
                searchKeywords: Translator.PeopleSettings_SearchKeywords),
            new(null, Translator.SettingsOptions_ToDoSection, string.Empty, "\uE823", isSeparator: true),
            new(WinoPage.ToDoPreferenceSettingsPage,
                Translator.ToDoSettings_Title,
                Translator.ToDoSettings_Description,
                "\uE713",
                searchKeywords: Translator.ToDoSettings_SearchKeywords)
        ];
    }

    /// <summary>
    /// The pane projection of <see cref="GetNavigationItems"/>. Entries before the first separator
    /// stay flat at the root; every separator afterwards becomes a collapsible group holding the
    /// entries that follow it.
    /// <para>
    /// This is a presentation shape only. Groups are never navigation targets, so
    /// <see cref="GetRootPage"/>, <see cref="GetPageTitle"/> and settings search continue to work
    /// against the flat list.
    /// </para>
    /// </summary>
    public static IReadOnlyList<SettingsNavigationPaneNode> GetPaneNodes(string manageAccountsDescription = "")
    {
        var nodes = new List<SettingsNavigationPaneNode>();
        List<SettingsNavigationItemInfo> currentGroupItems = null;

        foreach (var item in GetNavigationItems(manageAccountsDescription))
        {
            if (item.IsSeparator)
            {
                currentGroupItems = [];
                nodes.Add(new SettingsNavigationPaneNode(item.Title, item.Glyph, currentGroupItems));
                continue;
            }

            if (!item.PageType.HasValue)
                continue;

            if (currentGroupItems is null)
            {
                nodes.Add(new SettingsNavigationPaneNode(item));
                continue;
            }

            currentGroupItems.Add(item);
        }

        // A separator with nothing under it would render as an empty, unopenable group.
        return [.. nodes.Where(node => !node.IsGroup || node.Children.Count > 0)];
    }

    /// <summary>
    /// Finds the group that owns a page, so the pane can expand it when navigation or settings
    /// search lands inside a collapsed group. Returns null for a root-level page.
    /// </summary>
    public static string GetOwningGroupTitle(WinoPage pageType, string manageAccountsDescription = "")
    {
        var rootPage = GetRootPage(pageType);

        foreach (var node in GetPaneNodes(manageAccountsDescription))
        {
            if (node.IsGroup && node.Children.Any(child => child.PageType == rootPage))
                return node.Title;
        }

        return null;
    }

    public static IReadOnlyList<SettingsNavigationItemInfo> Search(string query, string manageAccountsDescription = "")
        => Search(query, manageAccountsDescription, []);

    public static IReadOnlyList<SettingsNavigationItemInfo> Search(
        string query,
        string manageAccountsDescription,
        IEnumerable<SettingsNavigationItemInfo> additionalItems)
    {
        if (string.IsNullOrWhiteSpace(query))
            return [];

        var normalizedQuery = NormalizeSearchText(query);

        if (string.IsNullOrWhiteSpace(normalizedQuery))
            return [];

        var queryTerms = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return GetNavigationItems(manageAccountsDescription)
            .Concat(additionalItems ?? [])
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
            WinoPage.WinoIntelligencePage => Translator.WinoIntelligence_SettingsTitle,
            WinoPage.IntelligenceCoveragePage => Translator.SemanticIndex_CoverageEditorTitle,
            WinoPage.PersonalizationPage => Translator.SettingsPersonalization_Title,
            WinoPage.ApplicationThemeGalleryPage => Translator.ApplicationThemeGallery_Title,
            WinoPage.ApplicationThemeEditorPage => Translator.ApplicationThemeEditor_CreateTitle,
            WinoPage.AboutPage => Translator.SettingsAbout_Title,
            WinoPage.MessageListPage => Translator.SettingsMessageList_Title,
            WinoPage.MailNotificationSettingsPage => Translator.SettingsMailNotifications_Title,
            WinoPage.UnreadBadgeSettingsPage => Translator.UnreadBadges_Title,
            WinoPage.AccountUnreadBadgePage => Translator.UnreadBadges_Title,
            WinoPage.ReadComposePanePage => Translator.SettingsReadComposePane_Title,
            WinoPage.AppPreferencesPage => Translator.SettingsGeneral_Title,
            WinoPage.MailPreferencesPage => Translator.SettingsMailPreferences_Title,
            WinoPage.BackupRestorePage => Translator.SettingsBackupRestore_Title,
            WinoPage.CalendarSettingsPage => Translator.CalendarSettings_Preferences_Title,
            WinoPage.CalendarRenderingSettingsPage => Translator.CalendarSettings_Rendering_Title,
            WinoPage.CalendarNotificationSettingsPage => Translator.CalendarSettings_Notifications_Title,
            WinoPage.CalendarPreferenceSettingsPage => Translator.CalendarSettings_Preferences_Title,
            WinoPage.ContactsPreferenceSettingsPage => Translator.PeopleSettings_Title,
            WinoPage.ToDoPreferenceSettingsPage => Translator.ToDoSettings_Title,
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
            WinoPage.MailFiltersPage => WinoPage.ManageAccountsPage,
            WinoPage.MailFilterEditorPage => WinoPage.ManageAccountsPage,
            WinoPage.SignatureManagementPage => WinoPage.ManageAccountsPage,
            WinoPage.ImapCalDavSettingsPage => WinoPage.ManageAccountsPage,
            WinoPage.AccountUnreadBadgePage => WinoPage.ManageAccountsPage,
            WinoPage.ProviderSelectionPage => WinoPage.ManageAccountsPage,
            WinoPage.SpecialImapCredentialsPage => WinoPage.ManageAccountsPage,
            WinoPage.AccountSetupProgressPage => WinoPage.ManageAccountsPage,
            WinoPage.CreateEmailTemplatePage => WinoPage.EmailTemplatesPage,
            WinoPage.ApplicationThemeGalleryPage => WinoPage.PersonalizationPage,
            WinoPage.ApplicationThemeEditorPage => WinoPage.PersonalizationPage,
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

    private const string CalendarRenderingIconPathData = "F1 M 15.078125 1.25 C 15.566406 1.25 16.033527 1.349285 16.479492 1.547852 C 16.925455 1.74642 17.31608 2.013348 17.651367 2.348633 C 17.986652 2.68392 18.25358 3.074545 18.452148 3.520508 C 18.650715 3.966473 18.75 4.433594 18.75 4.921875 L 18.75 15.078125 C 18.75 15.566406 18.650715 16.033529 18.452148 16.479492 C 18.25358 16.925455 17.986652 17.31608 17.651367 17.651367 C 17.31608 17.986654 16.925455 18.25358 16.479492 18.452148 C 16.033527 18.650717 15.566406 18.75 15.078125 18.75 L 4.921875 18.75 C 4.433594 18.75 3.966471 18.650717 3.520508 18.452148 C 3.074544 18.25358 2.683919 17.986654 2.348633 17.651367 C 2.013346 17.31608 1.746419 16.925455 1.547852 16.479492 C 1.349284 16.033529 1.25 15.566406 1.25 15.078125 L 1.25 4.921875 C 1.25 4.433594 1.349284 3.966473 1.547852 3.520508 C 1.746419 3.074545 2.013346 2.68392 2.348633 2.348633 C 2.683919 2.013348 3.074544 1.74642 3.520508 1.547852 C 3.966471 1.349285 4.433594 1.25 4.921875 1.25 Z M 4.951172 2.5 C 4.625651 2.5 4.314778 2.566732 4.018555 2.700195 C 3.722331 2.83366 3.461914 3.012695 3.237305 3.237305 C 3.012695 3.461914 2.833659 3.722332 2.700195 4.018555 C 2.566732 4.314779 2.5 4.625651 2.5 4.951172 L 2.5 5 L 17.5 5 L 17.5 4.951172 C 17.5 4.625651 17.433268 4.314779 17.299805 4.018555 C 17.16634 3.722332 16.987305 3.461914 16.762695 3.237305 C 16.538086 3.012695 16.277668 2.83366 15.981445 2.700195 C 15.685221 2.566732 15.374349 2.5 15.048828 2.5 Z M 15.048828 17.5 C 15.374349 17.5 15.685221 17.433268 15.981445 17.299805 C 16.277668 17.166342 16.538086 16.987305 16.762695 16.762695 C 16.987305 16.538086 17.16634 16.27767 17.299805 15.981445 C 17.433268 15.685222 17.5 15.37435 17.5 15.048828 L 17.5 6.25 L 2.5 6.25 L 2.5 15.048828 C 2.5 15.37435 2.566732 15.685222 2.700195 15.981445 C 2.833659 16.27767 3.012695 16.538086 3.237305 16.762695 C 3.461914 16.987305 3.722331 17.166342 4.018555 17.299805 C 4.314778 17.433268 4.625651 17.5 4.951172 17.5 Z M 16.25 9.375 C 16.25 9.544271 16.18815 9.690756 16.064453 9.814453 C 15.940754 9.938151 15.79427 10 15.625 10 L 13.642578 10 C 13.577474 10.188803 13.486328 10.359701 13.369141 10.512695 C 13.251953 10.66569 13.115234 10.797526 12.958984 10.908203 C 12.802734 11.018881 12.633463 11.103516 12.451172 11.162109 C 12.26888 11.220703 12.076822 11.25 11.875 11.25 C 11.673177 11.25 11.481119 11.220703 11.298828 11.162109 C 11.116536 11.103516 10.947266 11.018881 10.791016 10.908203 C 10.634766 10.797526 10.498047 10.66569 10.380859 10.512695 C 10.263672 10.359701 10.172525 10.188803 10.107422 10 L 4.375 10 C 4.205729 10 4.059245 9.938151 3.935547 9.814453 C 3.811849 9.690756 3.75 9.544271 3.75 9.375 C 3.75 9.205729 3.811849 9.059245 3.935547 8.935547 C 4.059245 8.81185 4.205729 8.75 4.375 8.75 L 10.107422 8.75 C 10.172525 8.561198 10.263672 8.3903 10.380859 8.237305 C 10.498047 8.084311 10.634766 7.952475 10.791016 7.841797 C 10.947266 7.73112 11.116536 7.646484 11.298828 7.587891 C 11.481119 7.529297 11.673177 7.5 11.875 7.5 C 12.076822 7.5 12.26888 7.529297 12.451172 7.587891 C 12.633463 7.646484 12.802734 7.73112 12.958984 7.841797 C 13.115234 7.952475 13.251953 8.084311 13.369141 8.237305 C 13.486328 8.3903 13.577474 8.561198 13.642578 8.75 L 15.625 8.75 C 15.79427 8.75 15.940754 8.81185 16.064453 8.935547 C 16.18815 9.059245 16.25 9.205729 16.25 9.375 Z M 16.25 14.375 C 16.25 14.544271 16.18815 14.690756 16.064453 14.814453 C 15.940754 14.938151 15.79427 15 15.625 15 L 9.892578 15 C 9.827474 15.188803 9.736328 15.359701 9.619141 15.512695 C 9.501953 15.66569 9.365234 15.797526 9.208984 15.908203 C 9.052734 16.018881 8.883463 16.103516 8.701172 16.162109 C 8.51888 16.220703 8.326822 16.25 8.125 16.25 C 7.923177 16.25 7.731119 16.220703 7.548828 16.162109 C 7.366536 16.103516 7.197266 16.018881 7.041016 15.908203 C 6.884766 15.797526 6.748047 15.66569 6.630859 15.512695 C 6.513672 15.359701 6.422526 15.188803 6.357422 15 L 4.375 15 C 4.205729 15 4.059245 14.938151 3.935547 14.814453 C 3.811849 14.690756 3.75 14.544271 3.75 14.375 C 3.75 14.205729 3.811849 14.059245 3.935547 13.935547 C 4.059245 13.81185 4.205729 13.75 4.375 13.75 L 6.357422 13.75 C 6.422526 13.561198 6.513672 13.3903 6.630859 13.237305 C 6.748047 13.084311 6.884766 12.952475 7.041016 12.841797 C 7.197266 12.73112 7.366536 12.646484 7.548828 12.587891 C 7.731119 12.529297 7.923177 12.5 8.125 12.5 C 8.326822 12.5 8.51888 12.529297 8.701172 12.587891 C 8.883463 12.646484 9.052734 12.73112 9.208984 12.841797 C 9.365234 12.952475 9.501953 13.084311 9.619141 13.237305 C 9.736328 13.3903 9.827474 13.561198 9.892578 13.75 L 15.625 13.75 C 15.79427 13.75 15.940754 13.81185 16.064453 13.935547 C 16.18815 14.059245 16.25 14.205729 16.25 14.375 Z ";
}
