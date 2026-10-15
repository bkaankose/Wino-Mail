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
/// The Settings window's page list over the shared <see cref="SettingsCatalog"/> (groups, order, titles, glyphs and
/// visibility, the same list as the Windows pane and the iOS and Android Settings). The Mac adds only the sidebar
/// row shape, the default page for a request without one, and the root page description under a sub-page's title.
/// </summary>
public static class SettingsPageCatalog
{
    /// <summary>The page shown when the window opens without a request. The Windows home grid is replaced by the sidebar.</summary>
    public const WinoPage DefaultPage = SettingsCatalog.DefaultPage;

    public static IReadOnlyList<SettingsSidebarEntry> GetSidebarEntries()
        => SettingsCatalog.GetEntries().Select(ToSidebar).ToList();

    /// <summary>Search results in the shared ranking (title, keywords, description), without group headers.</summary>
    public static IReadOnlyList<SettingsSidebarEntry> Search(string query)
        => SettingsCatalog.Search(query).Select(ToSidebar).ToList();

    public static WinoPage RootPage(WinoPage page) => page switch
    {
        WinoPage.SettingOptionsPage or WinoPage.SettingsPage => DefaultPage,
        _ => SettingsCatalog.RootPage(page)
    };

    public static string Title(WinoPage page) => SettingsCatalog.Title(page);

    /// <summary>The Windows page description shown under the breadcrumb title (a sub-page shows its root page's).</summary>
    public static string Description(WinoPage page)
    {
        if (page is WinoPage.WinoIntelligenceManagementPage or WinoPage.IntelligenceCoveragePage) return SettingsCatalog.Description(page);
        return SettingsCatalog.RootDescription(RootPage(page));
    }

    /// <summary>The Windows pane glyph of the page's root entry.</summary>
    public static WinoIconGlyph Glyph(WinoPage page) => SettingsCatalog.Glyph(RootPage(page));

    private static SettingsSidebarEntry ToSidebar(SettingsCatalogEntry entry)
        => new(entry.Title, entry.Page, entry.Icon, entry.Description, entry.Keywords);
}
