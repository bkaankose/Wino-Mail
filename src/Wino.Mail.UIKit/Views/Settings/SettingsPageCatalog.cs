using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Settings;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>One row of the Settings root list: a page with its Windows title, description and glyph.</summary>
public sealed record SettingsRootEntry(string Title, WinoPage Page, WinoIconGlyph Icon, string Description = "");

/// <summary>A titled run of root rows (a Windows settings pane group); the first group has no title.</summary>
public sealed record SettingsRootSection(string? Title, List<SettingsRootEntry> Entries);

/// <summary>
/// The Settings root list on iOS (the Mac SettingsPageCatalog). Groups, order, titles and glyphs come from the shared
/// <see cref="SettingsNavigationInfoProvider"/>, so the list matches the Windows settings pane; the shared
/// <see cref="SettingsPageAvailability"/> rule hides S/MIME and Wino Intelligence, and <see cref="IsSupportedOnDevice"/>
/// drops the pages that have no meaning on a phone or tablet.
/// </summary>
public static class SettingsPageCatalog
{
    /// <summary>Windows hides the S/MIME page when the platform has no certificate support; set when services are ready.</summary>
    public static bool IsSmimeAvailable { get; set; }

    /// <summary>Keyboard shortcuts are listed on iPad only (hardware keyboards). Set when services are ready.</summary>
    public static bool IsPad { get; set; }

    /// <summary>Same rule as the shared SettingsMenuProvider: the Wino Intelligence page needs the entitlement to its surfaces.</summary>
    public static Func<bool>? CanAccessIntelligence { get; set; }

    /// <summary>
    /// Pages without an iOS meaning: the Windows home grid (the root list replaces it), the menu bar Companion, Backup
    /// and restore (the Windows/Mac file flow), and Keyboard shortcuts on iPhone. Startup, hover, density and reading-pane
    /// options are removed inside their pages.
    /// </summary>
    public static bool IsSupportedOnDevice(WinoPage page) => page switch
    {
        WinoPage.SettingOptionsPage or WinoPage.SettingsPage => false,
        WinoPage.CompanionSettingsPage or WinoPage.BackupRestorePage => false,
        WinoPage.KeyboardShortcutsPage => IsPad,
        _ => true
    };

    public static bool IsVisible(WinoPage page)
        => IsSupportedOnDevice(page)
            && SettingsPageAvailability.IsAvailable(page, IsSmimeAvailable, page == WinoPage.WinoIntelligencePage && (CanAccessIntelligence?.Invoke() ?? false));

    /// <summary>The root list in the Windows pane order: the untitled account rows first, then each titled group.</summary>
    public static List<SettingsRootSection> GetSections()
    {
        var sections = new List<SettingsRootSection> { new(null, []) };
        foreach (var item in SettingsNavigationInfoProvider.GetNavigationItems())
        {
            if (item.IsSeparator)
            {
                sections.Add(new SettingsRootSection(item.Title, []));
                continue;
            }
            if (item.PageType is not { } page || !IsVisible(page)) continue;
            sections[^1].Entries.Add(new SettingsRootEntry(item.Title, page, item.Icon, DeviceDescription(page) ?? item.Description ?? string.Empty));
        }
        return sections.Where(section => section.Entries.Count > 0).ToList();
    }

    /// <summary>Search results in the shared ranking (title, keywords, description).</summary>
    public static List<SettingsRootEntry> Search(string query)
        => SettingsNavigationInfoProvider.Search(query)
            .Where(item => item.PageType is { } page && IsVisible(page))
            .Select(item => new SettingsRootEntry(item.Title, item.PageType ?? WinoPage.SettingsPage, item.Icon,
                (item.PageType is { } page ? DeviceDescription(page) : null) ?? item.Description ?? string.Empty))
            .ToList();

    public static WinoPage RootPage(WinoPage page) => page switch
    {
        WinoPage.WinoIntelligenceManagementPage or WinoPage.IntelligenceCoveragePage => WinoPage.WinoIntelligencePage,
        _ => SettingsNavigationInfoProvider.GetRootPage(page)
    };

    public static string Title(WinoPage page)
    {
        if (page is WinoPage.SettingsPage or WinoPage.SettingOptionsPage) return Translator.MenuSettings;
        try { return SettingsNavigationInfoProvider.GetPageTitle(page); }
        catch (InvalidOperationException) { return page.ToString(); }
    }

    /// <summary>
    /// iOS wording for shared entries whose Windows and Mac description names desktop-only settings: General has only the
    /// display language on iOS (no startup or close behaviour), so it uses the language card's description.
    /// </summary>
    private static string? DeviceDescription(WinoPage page) => page switch
    {
        WinoPage.AppPreferencesPage => Translator.SettingsLanguage_Description,
        _ => null
    };

    /// <summary>The Windows page description; sub-pages (account pages, editors) have none of their own.</summary>
    public static string Description(WinoPage page)
    {
        if (page == WinoPage.WinoIntelligenceManagementPage) return Translator.SemanticIndex_PageDescription;
        if (page == WinoPage.IntelligenceCoveragePage) return Translator.SemanticIndex_CoverageEditorPageDescription;
        if (RootPage(page) != page) return string.Empty;
        if (DeviceDescription(page) is { } device) return device;
        try { return SettingsNavigationInfoProvider.GetInfo(page).Description ?? string.Empty; }
        catch (InvalidOperationException) { return string.Empty; }
    }

    /// <summary>The Windows pane glyph of the page's root entry.</summary>
    public static WinoIconGlyph Glyph(WinoPage page)
    {
        try { return SettingsNavigationInfoProvider.GetInfo(RootPage(page)).Icon; }
        catch (InvalidOperationException) { return WinoIconGlyph.Settings; }
    }
}
