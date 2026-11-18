#nullable enable
using System;
using System.Collections.Generic;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Models.Settings;

/// <summary>One row of a Settings page list: a group header (no page) or a page with its Windows title, glyph and description.</summary>
public sealed record SettingsCatalogEntry(string Title, WinoPage? Page, WinoIconGlyph Icon, string Description = "", string Keywords = "")
{
    public bool IsHeader => Page is null;
}

/// <summary>A titled run of Settings rows (a Windows settings pane group); the first group has no title.</summary>
public sealed record SettingsCatalogSection(string? Title, List<SettingsCatalogEntry> Entries);

/// <summary>
/// The page lookups of the Settings list that do not depend on the platform or the entitlement: the default page, the
/// list row a page belongs to, titles, glyphs and groups. The list itself is <see cref="ISettingsCatalog"/>.
/// </summary>
public static class SettingsCatalog
{
    /// <summary>The page a list-detail Settings surface shows when it opens without a request (Windows home grid replaced).</summary>
    public const WinoPage DefaultPage = WinoPage.AppPreferencesPage;

    /// <summary>The list row a page belongs to. Windows opens the Intelligence sub-pages from the Wino Intelligence page.</summary>
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

    /// <summary>The Windows pane glyph of the page's list row.</summary>
    public static WinoIconGlyph Glyph(WinoPage page)
    {
        try { return SettingsNavigationInfoProvider.GetInfo(RootPage(page)).Icon; }
        catch (InvalidOperationException) { return WinoIconGlyph.Settings; }
    }

    /// <summary>The title of the group that lists the page's row (General, Mail…); null for the untitled account rows.</summary>
    public static string? GroupTitle(WinoPage page)
    {
        var root = RootPage(page);
        string? group = null;
        foreach (var item in SettingsNavigationInfoProvider.GetNavigationItems())
        {
            if (item.IsSeparator) group = item.Title;
            else if (item.PageType == root) return group;
        }
        return null;
    }
}
