using System;
using System.Collections.Generic;
using System.Linq;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Navigation;

namespace MailCal.Core.Domain.Models.Settings;

public sealed class SettingsNavigationItemInfo(
    MailCalPage? pageType,
    string title,
    string description,
    MailCalIconGlyph icon = MailCalIconGlyph.None,
    bool isSeparator = false,
    string searchKeywords = "",
    SettingsNavigationRoute navigationRoute = null)
{
    public MailCalPage? PageType { get; } = pageType;
    public string Title { get; } = title;
    public string Description { get; } = description;
    public MailCalIconGlyph Icon { get; } = icon;
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

    public SettingsNavigationPaneNode(string title, MailCalIconGlyph icon, IReadOnlyList<SettingsNavigationItemInfo> children)
    {
        Title = title;
        Icon = icon;
        Children = children;
    }

    /// <summary>The page this row navigates to. Null for a group.</summary>
    public SettingsNavigationItemInfo Item { get; }

    public string Title { get; }

    public MailCalIconGlyph Icon { get; }

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
            new(MailCalPage.SettingOptionsPage,
                Translator.SettingsHome_Title,
                Translator.SettingsOptions_HeroDescription,
                MailCalIconGlyph.Home),
            new(MailCalPage.ManageAccountsPage,
                Translator.SettingsManageAccountSettings_Title,
                manageAccountsDescription,
                MailCalIconGlyph.ManageAccounts,
                searchKeywords: Translator.SettingsSearch_ManageAccounts_Keywords),
            new(null, Translator.SettingsOptions_GeneralSection, string.Empty, MailCalIconGlyph.Settings, isSeparator: true),
            new(MailCalPage.AppPreferencesPage,
                Translator.SettingsGeneral_Title,
                Translator.SettingsGeneral_Description,
                MailCalIconGlyph.Desktop,
                searchKeywords: Translator.SettingsSearch_General_Keywords),
            new(MailCalPage.NotificationSettingsPage,
                Translator.NotificationSettings_Title,
                Translator.NotificationSettings_Description,
                MailCalIconGlyph.Alert,
                searchKeywords: Translator.NotificationSettings_SearchKeywords),
            new(MailCalPage.CompanionSettingsPage,
                Translator.CompanionSettings_Title,
                Translator.CompanionSettings_Description,
                MailCalIconGlyph.Companion,
                searchKeywords: Translator.CompanionSettings_SearchKeywords),
            new(MailCalPage.PersonalizationPage,
                Translator.SettingsPersonalization_Title,
                Translator.SettingsPersonalization_Description,
                MailCalIconGlyph.PaintBrush,
                searchKeywords: Translator.SettingsSearch_Personalization_Keywords),
            new(MailCalPage.KeyboardShortcutsPage,
                Translator.Settings_KeyboardShortcuts_Title,
                Translator.Settings_KeyboardShortcuts_Description,
                MailCalIconGlyph.Keyboard,
                searchKeywords: Translator.SettingsSearch_KeyboardShortcuts_Keywords),
            new(MailCalPage.BackupRestorePage,
                Translator.SettingsBackupRestore_Title,
                Translator.SettingsBackupRestore_Description_Device,
                MailCalIconGlyph.ArrowSyncCircle,
                searchKeywords: Translator.SettingsSearch_BackupRestore_Keywords),
            new(MailCalPage.AboutPage,
                Translator.SettingsAbout_Title,
                Translator.SettingsAbout_Description,
                MailCalIconGlyph.Info,
                searchKeywords: Translator.SettingsSearch_About_Keywords),
            new(null, Translator.SettingsOptions_MailSection, string.Empty, MailCalIconGlyph.Mail, isSeparator: true),
            new(MailCalPage.MailPreferencesPage,
                Translator.SettingsMailPreferences_Title,
                Translator.SettingsMailPreferences_Description,
                MailCalIconGlyph.Settings,
                searchKeywords: Translator.SettingsSearch_MailPreferences_Keywords),
            new(MailCalPage.MessageListPage,
                Translator.SettingsMessageList_Title,
                Translator.SettingsMessageList_Description,
                MailCalIconGlyph.List,
                searchKeywords: Translator.SettingsSearch_MessageList_Keywords),
            new(MailCalPage.UnreadBadgeSettingsPage,
                Translator.UnreadBadges_Title,
                Translator.UnreadBadges_Description,
                MailCalIconGlyph.AlertBadge,
                searchKeywords: Translator.SettingsSearch_UnreadBadges_Keywords),
            new(MailCalPage.ReadComposePanePage,
                Translator.SettingsReadComposePane_Title,
                Translator.SettingsReadComposePane_Description,
                MailCalIconGlyph.Message,
                searchKeywords: Translator.SettingsSearch_ReadComposePane_Keywords),
            new(MailCalPage.SignatureAndEncryptionPage,
                Translator.SettingsSignatureAndEncryption_Title,
                Translator.SettingsSignatureAndEncryption_Description,
                MailCalIconGlyph.Signature,
                searchKeywords: Translator.SettingsSearch_SignatureAndEncryption_Keywords),
            new(MailCalPage.EmailTemplatesPage,
                Translator.SettingsEmailTemplates_Title,
                Translator.SettingsEmailTemplates_Description,
                MailCalIconGlyph.Edit),
            new(MailCalPage.StoragePage,
                Translator.SettingsStorage_Title,
                Translator.SettingsStorage_Description,
                MailCalIconGlyph.Storage,
                searchKeywords: Translator.SettingsSearch_Storage_Keywords),
            new(null, Translator.SettingsOptions_CalendarSection, string.Empty, MailCalIconGlyph.Calendar, isSeparator: true),
            new(MailCalPage.CalendarPreferenceSettingsPage,
                Translator.CalendarSettings_Preferences_Title,
                Translator.CalendarSettings_Preferences_Description,
                MailCalIconGlyph.Settings,
                searchKeywords: Translator.SettingsSearch_CalendarSettings_Keywords),
            new(MailCalPage.CalendarRenderingSettingsPage,
                Translator.CalendarSettings_Rendering_Title,
                Translator.CalendarSettings_Rendering_Description,
                MailCalIconGlyph.CalendarMonth,
                searchKeywords: Translator.SettingsSearch_CalendarSettings_Keywords),
            new(null, Translator.SettingsOptions_PeopleSection, string.Empty, MailCalIconGlyph.People, isSeparator: true),
            new(MailCalPage.ContactsPreferenceSettingsPage,
                Translator.PeopleSettings_Title,
                Translator.PeopleSettings_Description,
                MailCalIconGlyph.Settings,
                searchKeywords: Translator.PeopleSettings_SearchKeywords),
            new(null, Translator.SettingsOptions_ToDoSection, string.Empty, MailCalIconGlyph.CheckmarkCircle, isSeparator: true),
            new(MailCalPage.ToDoPreferenceSettingsPage,
                Translator.ToDoSettings_Title,
                Translator.ToDoSettings_Description,
                MailCalIconGlyph.Settings,
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
    public static string GetOwningGroupTitle(MailCalPage pageType, string manageAccountsDescription = "")
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
            .Where(item => item.PageType.HasValue && !item.IsSeparator && item.PageType.Value != MailCalPage.SettingOptionsPage)
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

    public static SettingsNavigationItemInfo GetInfo(MailCalPage pageType, string manageAccountsDescription = "")
    {
        var rootPage = GetRootPage(pageType);
        return GetNavigationItems(manageAccountsDescription)
            .FirstOrDefault(item => item.PageType == rootPage)
            ?? GetNavigationItems(manageAccountsDescription).First(item => item.PageType == MailCalPage.SettingOptionsPage);
    }

    public static string GetPageTitle(MailCalPage pageType)
        => pageType switch
        {
            MailCalPage.SettingOptionsPage => Translator.MenuSettings,
            MailCalPage.ManageAccountsPage => Translator.SettingsManageAccountSettings_Title,
            MailCalPage.AccountManagementPage => Translator.SettingsManageAccountSettings_Title,
            MailCalPage.PersonalizationPage => Translator.SettingsPersonalization_Title,
            MailCalPage.ApplicationThemeGalleryPage => Translator.ApplicationThemeGallery_Title,
            MailCalPage.ApplicationThemeEditorPage => Translator.ApplicationThemeEditor_CreateTitle,
            MailCalPage.AboutPage => Translator.SettingsAbout_Title,
            MailCalPage.MessageListPage => Translator.SettingsMessageList_Title,
            MailCalPage.NotificationSettingsPage => Translator.NotificationSettings_Title,
            MailCalPage.UnreadBadgeSettingsPage => Translator.UnreadBadges_Title,
            MailCalPage.AccountUnreadBadgePage => Translator.UnreadBadges_Title,
            MailCalPage.ReadComposePanePage => Translator.SettingsReadComposePane_Title,
            MailCalPage.AppPreferencesPage => Translator.SettingsGeneral_Title,
            MailCalPage.CompanionSettingsPage => Translator.CompanionSettings_Title,
            MailCalPage.MailPreferencesPage => Translator.SettingsMailPreferences_Title,
            MailCalPage.BackupRestorePage => Translator.SettingsBackupRestore_Title,
            MailCalPage.CalendarSettingsPage => Translator.CalendarSettings_Preferences_Title,
            MailCalPage.CalendarRenderingSettingsPage => Translator.CalendarSettings_Rendering_Title,
            MailCalPage.CalendarPreferenceSettingsPage => Translator.CalendarSettings_Preferences_Title,
            MailCalPage.ContactsPreferenceSettingsPage => Translator.PeopleSettings_Title,
            MailCalPage.ToDoPreferenceSettingsPage => Translator.ToDoSettings_Title,
            MailCalPage.SignatureAndEncryptionPage => Translator.SettingsSignatureAndEncryption_Title,
            MailCalPage.KeyboardShortcutsPage => Translator.Settings_KeyboardShortcuts_Title,
            MailCalPage.StoragePage => Translator.SettingsStorage_Title,
            MailCalPage.EmailTemplatesPage => Translator.SettingsEmailTemplates_Title,
            MailCalPage.CreateEmailTemplatePage => Translator.SettingsEmailTemplates_Title,
            _ => GetInfo(pageType).Title
        };

    public static MailCalPage GetRootPage(MailCalPage pageType)
        => pageType switch
        {
            MailCalPage.AccountManagementPage => MailCalPage.ManageAccountsPage,
            MailCalPage.AccountDetailsPage => MailCalPage.ManageAccountsPage,
            MailCalPage.MergedAccountDetailsPage => MailCalPage.ManageAccountsPage,
            MailCalPage.AliasManagementPage => MailCalPage.ManageAccountsPage,
            MailCalPage.FolderCustomizationPage => MailCalPage.ManageAccountsPage,
            MailCalPage.MailCategoryManagementPage => MailCalPage.ManageAccountsPage,
            MailCalPage.MailFiltersPage => MailCalPage.ManageAccountsPage,
            MailCalPage.MailFilterEditorPage => MailCalPage.ManageAccountsPage,
            MailCalPage.SignatureManagementPage => MailCalPage.ManageAccountsPage,
            MailCalPage.ImapCalDavSettingsPage => MailCalPage.ManageAccountsPage,
            MailCalPage.AccountUnreadBadgePage => MailCalPage.UnreadBadgeSettingsPage,
            MailCalPage.ProviderSelectionPage => MailCalPage.ManageAccountsPage,
            MailCalPage.SpecialImapCredentialsPage => MailCalPage.ManageAccountsPage,
            MailCalPage.AccountSetupProgressPage => MailCalPage.ManageAccountsPage,
            MailCalPage.CreateEmailTemplatePage => MailCalPage.EmailTemplatesPage,
            MailCalPage.ApplicationThemeGalleryPage => MailCalPage.PersonalizationPage,
            MailCalPage.ApplicationThemeEditorPage => MailCalPage.PersonalizationPage,
            MailCalPage.CalendarSettingsPage => MailCalPage.CalendarPreferenceSettingsPage,
            MailCalPage.CalendarAccountSettingsPage => MailCalPage.CalendarPreferenceSettingsPage,
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
