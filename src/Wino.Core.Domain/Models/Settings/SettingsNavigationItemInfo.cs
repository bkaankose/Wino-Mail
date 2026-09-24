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
    WinoIconGlyph icon = WinoIconGlyph.None,
    bool isSeparator = false,
    string searchKeywords = "",
    SettingsNavigationRoute navigationRoute = null)
{
    public WinoPage? PageType { get; } = pageType;
    public string Title { get; } = title;
    public string Description { get; } = description;
    public WinoIconGlyph Icon { get; } = icon;
    public bool IsSeparator { get; } = isSeparator;
    public string SearchKeywords { get; } = searchKeywords;
    public SettingsNavigationRoute NavigationRoute { get; } = navigationRoute;
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
        Icon = item.Icon;
    }

    public SettingsNavigationPaneNode(string title, WinoIconGlyph icon, IReadOnlyList<SettingsNavigationItemInfo> children)
    {
        Title = title;
        Icon = icon;
        Children = children;
    }

    /// <summary>The page this row navigates to. Null for a group.</summary>
    public SettingsNavigationItemInfo Item { get; }

    public string Title { get; }

    public WinoIconGlyph Icon { get; }

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
                WinoIconGlyph.Home),
            new(WinoPage.ManageAccountsPage,
                Translator.SettingsManageAccountSettings_Title,
                manageAccountsDescription,
                WinoIconGlyph.ManageAccounts,
                searchKeywords: Translator.SettingsSearch_ManageAccounts_Keywords),
            new(WinoPage.WinoAccountManagementPage,
                Translator.WinoAccount_SettingsSection_Title,
                Translator.WinoAccount_SettingsSection_Description,
                WinoIconGlyph.Person,
                searchKeywords: string.Empty),
            new(WinoPage.WinoIntelligencePage,
                Translator.WinoIntelligence_SettingsTitle,
                Translator.WinoIntelligence_SettingsDescription,
                WinoIconGlyph.WinoIntelligence,
                searchKeywords: Translator.SettingsSearch_WinoIntelligence_Keywords),
            new(null, Translator.SettingsOptions_GeneralSection, string.Empty, WinoIconGlyph.Settings, isSeparator: true),
            new(WinoPage.AppPreferencesPage,
                Translator.SettingsGeneral_Title,
                Translator.SettingsGeneral_Description,
                WinoIconGlyph.Desktop,
                searchKeywords: Translator.SettingsSearch_General_Keywords),
            new(WinoPage.NotificationSettingsPage,
                Translator.NotificationSettings_Title,
                Translator.NotificationSettings_Description,
                WinoIconGlyph.Alert,
                searchKeywords: Translator.NotificationSettings_SearchKeywords),
            new(WinoPage.CompanionSettingsPage,
                Translator.CompanionSettings_Title,
                Translator.CompanionSettings_Description,
                WinoIconGlyph.Companion,
                searchKeywords: Translator.CompanionSettings_SearchKeywords),
            new(WinoPage.PersonalizationPage,
                Translator.SettingsPersonalization_Title,
                Translator.SettingsPersonalization_Description,
                WinoIconGlyph.PaintBrush,
                searchKeywords: Translator.SettingsSearch_Personalization_Keywords),
            new(WinoPage.KeyboardShortcutsPage,
                Translator.Settings_KeyboardShortcuts_Title,
                Translator.Settings_KeyboardShortcuts_Description,
                WinoIconGlyph.Keyboard,
                searchKeywords: Translator.SettingsSearch_KeyboardShortcuts_Keywords),
            new(WinoPage.BackupRestorePage,
                Translator.SettingsBackupRestore_Title,
                Translator.SettingsBackupRestore_Description,
                WinoIconGlyph.ArrowSyncCircle,
                searchKeywords: Translator.SettingsSearch_BackupRestore_Keywords),
            new(WinoPage.AboutPage,
                Translator.SettingsAbout_Title,
                Translator.SettingsAbout_Description,
                WinoIconGlyph.Info,
                searchKeywords: Translator.SettingsSearch_About_Keywords),
            new(null, Translator.SettingsOptions_MailSection, string.Empty, WinoIconGlyph.Mail, isSeparator: true),
            new(WinoPage.MailPreferencesPage,
                Translator.SettingsMailPreferences_Title,
                Translator.SettingsMailPreferences_Description,
                WinoIconGlyph.Settings,
                searchKeywords: Translator.SettingsSearch_MailPreferences_Keywords),
            new(WinoPage.MessageListPage,
                Translator.SettingsMessageList_Title,
                Translator.SettingsMessageList_Description,
                WinoIconGlyph.List,
                searchKeywords: Translator.SettingsSearch_MessageList_Keywords),
            new(WinoPage.UnreadBadgeSettingsPage,
                Translator.UnreadBadges_Title,
                Translator.UnreadBadges_Description,
                WinoIconGlyph.AlertBadge,
                searchKeywords: Translator.SettingsSearch_UnreadBadges_Keywords),
            new(WinoPage.ReadComposePanePage,
                Translator.SettingsReadComposePane_Title,
                Translator.SettingsReadComposePane_Description,
                WinoIconGlyph.Message,
                searchKeywords: Translator.SettingsSearch_ReadComposePane_Keywords),
            new(WinoPage.SignatureAndEncryptionPage,
                Translator.SettingsSignatureAndEncryption_Title,
                Translator.SettingsSignatureAndEncryption_Description,
                WinoIconGlyph.Signature,
                searchKeywords: Translator.SettingsSearch_SignatureAndEncryption_Keywords),
            new(WinoPage.EmailTemplatesPage,
                Translator.SettingsEmailTemplates_Title,
                Translator.SettingsEmailTemplates_Description,
                WinoIconGlyph.Edit),
            new(WinoPage.StoragePage,
                Translator.SettingsStorage_Title,
                Translator.SettingsStorage_Description,
                WinoIconGlyph.Storage,
                searchKeywords: Translator.SettingsSearch_Storage_Keywords),
            new(null, Translator.SettingsOptions_CalendarSection, string.Empty, WinoIconGlyph.Calendar, isSeparator: true),
            new(WinoPage.CalendarPreferenceSettingsPage,
                Translator.CalendarSettings_Preferences_Title,
                Translator.CalendarSettings_Preferences_Description,
                WinoIconGlyph.Settings,
                searchKeywords: Translator.SettingsSearch_CalendarSettings_Keywords),
            new(WinoPage.CalendarRenderingSettingsPage,
                Translator.CalendarSettings_Rendering_Title,
                Translator.CalendarSettings_Rendering_Description,
                WinoIconGlyph.CalendarMonth,
                searchKeywords: Translator.SettingsSearch_CalendarSettings_Keywords),
            new(null, Translator.SettingsOptions_PeopleSection, string.Empty, WinoIconGlyph.People, isSeparator: true),
            new(WinoPage.ContactsPreferenceSettingsPage,
                Translator.PeopleSettings_Title,
                Translator.PeopleSettings_Description,
                WinoIconGlyph.Settings,
                searchKeywords: Translator.PeopleSettings_SearchKeywords),
            new(null, Translator.SettingsOptions_ToDoSection, string.Empty, WinoIconGlyph.CheckmarkCircle, isSeparator: true),
            new(WinoPage.ToDoPreferenceSettingsPage,
                Translator.ToDoSettings_Title,
                Translator.ToDoSettings_Description,
                WinoIconGlyph.Settings,
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
                nodes.Add(new SettingsNavigationPaneNode(item.Title, item.Icon, currentGroupItems));
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
            WinoPage.NotificationSettingsPage => Translator.NotificationSettings_Title,
            WinoPage.UnreadBadgeSettingsPage => Translator.UnreadBadges_Title,
            WinoPage.AccountUnreadBadgePage => Translator.UnreadBadges_Title,
            WinoPage.ReadComposePanePage => Translator.SettingsReadComposePane_Title,
            WinoPage.AppPreferencesPage => Translator.SettingsGeneral_Title,
            WinoPage.CompanionSettingsPage => Translator.CompanionSettings_Title,
            WinoPage.MailPreferencesPage => Translator.SettingsMailPreferences_Title,
            WinoPage.BackupRestorePage => Translator.SettingsBackupRestore_Title,
            WinoPage.CalendarSettingsPage => Translator.CalendarSettings_Preferences_Title,
            WinoPage.CalendarRenderingSettingsPage => Translator.CalendarSettings_Rendering_Title,
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
            WinoPage.AccountUnreadBadgePage => WinoPage.UnreadBadgeSettingsPage,
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
}
