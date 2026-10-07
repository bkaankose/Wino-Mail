using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Settings;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>One row of the Settings window sidebar: a group header or a page, with its Windows glyph.</summary>
public sealed record SettingsSidebarEntry(string Title, WinoPage? Page, WinoIconGlyph Icon, string Description = "", string Keywords = "")
{
    public bool IsHeader => Page is null;
}

/// <summary>
/// The Settings window's page list. Groups, order, titles and glyphs come from the shared
/// <see cref="SettingsNavigationInfoProvider"/> so the Mac sidebar matches the Windows settings pane.
/// </summary>
public static class SettingsPageCatalog
{
    /// <summary>The page shown when the window opens without a request. The Windows home grid is replaced by the sidebar.</summary>
    public const WinoPage DefaultPage = WinoPage.AppPreferencesPage;

    /// <summary>Windows hides the S/MIME page when the platform has no certificate support; set from the composition root.</summary>
    public static bool IsSmimeAvailable { get; set; }

    /// <summary>
    /// Same rule as the shared SettingsMenuProvider: the Wino Intelligence page is listed only while the
    /// Wino Account entitlement allows its surfaces. Set from the composition root.
    /// </summary>
    public static Func<bool>? CanAccessIntelligence { get; set; }

    public static IReadOnlyList<SettingsSidebarEntry> GetSidebarEntries()
    {
        var entries = new List<SettingsSidebarEntry>();
        foreach (var item in SettingsNavigationInfoProvider.GetNavigationItems())
        {
            if (item.IsSeparator || item.PageType is null)
            {
                entries.Add(new SettingsSidebarEntry(item.Title, null, item.Icon));
                continue;
            }
            if (!IsVisible(item.PageType.Value)) continue;
            entries.Add(new SettingsSidebarEntry(item.Title, item.PageType, item.Icon, item.Description ?? string.Empty, item.SearchKeywords ?? string.Empty));
        }
        return entries;
    }

    private static bool IsVisible(WinoPage page) => page switch
    {
        WinoPage.SettingOptionsPage => false,
        WinoPage.SignatureAndEncryptionPage => IsSmimeAvailable,
        WinoPage.WinoIntelligencePage => CanAccessIntelligence?.Invoke() ?? false,
        _ => true
    };

    /// <summary>Search results in the shared ranking (title, keywords, description), without group headers.</summary>
    public static IReadOnlyList<SettingsSidebarEntry> Search(string query)
        => SettingsNavigationInfoProvider.Search(query)
            .Where(item => item.PageType is not null && IsVisible(item.PageType.Value))
            .Select(item => new SettingsSidebarEntry(item.Title, item.PageType, item.Icon, item.Description ?? string.Empty, item.SearchKeywords ?? string.Empty))
            .ToList();

    public static WinoPage RootPage(WinoPage page) => page switch
    {
        WinoPage.SettingOptionsPage or WinoPage.SettingsPage => DefaultPage,
        // Windows opens these from the Wino Intelligence page; keep its sidebar row and breadcrumb.
        WinoPage.WinoIntelligenceManagementPage or WinoPage.IntelligenceCoveragePage => WinoPage.WinoIntelligencePage,
        _ => SettingsNavigationInfoProvider.GetRootPage(page)
    };

    public static string Title(WinoPage page)
    {
        try { return SettingsNavigationInfoProvider.GetPageTitle(page); }
        catch { return page.ToString(); }
    }

    /// <summary>The Windows page description shown under the breadcrumb title.</summary>
    public static string Description(WinoPage page)
    {
        if (page == WinoPage.WinoIntelligenceManagementPage) return Translator.SemanticIndex_PageDescription;
        if (page == WinoPage.IntelligenceCoveragePage) return Translator.SemanticIndex_CoverageEditorPageDescription;
        try { return SettingsNavigationInfoProvider.GetInfo(RootPage(page)).Description ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>The Windows pane glyph of the page's root entry.</summary>
    public static WinoIconGlyph Glyph(WinoPage page)
    {
        try { return SettingsNavigationInfoProvider.GetInfo(RootPage(page)).Icon; }
        catch { return WinoIconGlyph.Settings; }
    }
}
