#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Platform;

namespace Wino.Core.Domain.Models.Settings;

/// <summary>One row of a Settings page list: a group header (no page) or a page with its Windows title, glyph and description.</summary>
public sealed record SettingsCatalogEntry(string Title, WinoPage? Page, WinoIconGlyph Icon, string Description = "", string Keywords = "")
{
    public bool IsHeader => Page is null;
}

/// <summary>A titled run of Settings rows (a Windows settings pane group); the first group has no title.</summary>
public sealed record SettingsCatalogSection(string? Title, List<SettingsCatalogEntry> Entries);

/// <summary>
/// The Settings page list for the native heads (the Mac sidebar, the iOS and Android root lists). Groups, order,
/// titles and glyphs come from <see cref="SettingsNavigationInfoProvider"/>, the source of the Windows settings pane, so
/// every platform lists the same pages in the same places; visibility follows <see cref="SettingsPageAvailability"/>
/// with the platform's <see cref="IPlatformCapabilities"/> and the Wino Intelligence entitlement. The shared
/// SettingsCatalogInitializer calls <see cref="Configure"/> at the start of runtime initialization on every head.
/// </summary>
public static class SettingsCatalog
{
    /// <summary>The page a list-detail Settings surface shows when it opens without a request (Windows home grid replaced).</summary>
    public const WinoPage DefaultPage = WinoPage.AppPreferencesPage;

    private static IPlatformCapabilities _capabilities = new PlatformCapabilities();
    private static Func<bool>? _canAccessIntelligence;

    /// <summary>The capabilities the list was configured with (all false until <see cref="Configure"/>).</summary>
    public static IPlatformCapabilities Capabilities => _capabilities;

    /// <summary>Sets the platform rules; <paramref name="canAccessIntelligence"/> is read whenever the list is built.</summary>
    public static void Configure(IPlatformCapabilities capabilities, Func<bool>? canAccessIntelligence)
    {
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _canAccessIntelligence = canAccessIntelligence;
    }

    public static bool CanAccessIntelligence => _canAccessIntelligence?.Invoke() ?? false;

    /// <summary>The Windows home grid is replaced by the list itself; every other page follows the shared availability rule.</summary>
    public static bool IsVisible(WinoPage page)
        => page is not (WinoPage.SettingOptionsPage or WinoPage.SettingsPage)
            && SettingsPageAvailability.IsAvailable(page, _capabilities, page == WinoPage.WinoIntelligencePage && CanAccessIntelligence);

    /// <summary>The list in the Windows pane order, with a header row before each titled group (the Mac sidebar shape).</summary>
    public static List<SettingsCatalogEntry> GetEntries()
    {
        var entries = new List<SettingsCatalogEntry>();
        foreach (var section in GetSections())
        {
            if (section.Title is { } title) entries.Add(new SettingsCatalogEntry(title, null, WinoIconGlyph.None));
            entries.AddRange(section.Entries);
        }
        return entries;
    }

    /// <summary>The list in the Windows pane order: the untitled account rows first, then each titled group. Empty groups are dropped.</summary>
    public static List<SettingsCatalogSection> GetSections()
    {
        var sections = new List<SettingsCatalogSection> { new(null, []) };
        foreach (var item in SettingsNavigationInfoProvider.GetNavigationItems())
        {
            if (item.IsSeparator)
            {
                sections.Add(new SettingsCatalogSection(item.Title, []));
                continue;
            }
            if (item.PageType is not { } page || !IsVisible(page)) continue;
            sections[^1].Entries.Add(Entry(item, page));
        }
        return sections.Where(section => section.Entries.Count > 0).ToList();
    }

    /// <summary>Search results in the shared ranking (title, keywords, description), without headers.</summary>
    public static List<SettingsCatalogEntry> Search(string query)
        => SettingsNavigationInfoProvider.Search(query ?? string.Empty)
            .Where(item => item.PageType is { } page && IsVisible(page))
            .Select(item => Entry(item, item.PageType ?? DefaultPage))
            .ToList();

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

    /// <summary>The description of a list row: the Windows text, or the platform's wording where Windows names settings it lacks.</summary>
    public static string RootDescription(WinoPage rootPage)
    {
        if (PlatformDescription(rootPage) is { } platform) return platform;
        try { return SettingsNavigationInfoProvider.GetInfo(rootPage).Description ?? string.Empty; }
        catch (InvalidOperationException) { return string.Empty; }
    }

    /// <summary>The Windows page description shown under a page title; sub-pages (account pages, editors) have none of their own.</summary>
    public static string Description(WinoPage page)
    {
        if (page == WinoPage.WinoIntelligenceManagementPage) return Translator.SemanticIndex_PageDescription;
        if (page == WinoPage.IntelligenceCoveragePage) return Translator.SemanticIndex_CoverageEditorPageDescription;
        return RootPage(page) == page ? RootDescription(page) : string.Empty;
    }

    /// <summary>The description under a breadcrumb title: the page's own, otherwise its list row's.</summary>
    public static string BreadcrumbDescription(WinoPage page)
        => Description(page) is { Length: > 0 } own ? own : RootDescription(RootPage(page));

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

    private static SettingsCatalogEntry Entry(SettingsNavigationItemInfo item, WinoPage page)
        => new(item.Title, page, item.Icon, PlatformDescription(page) ?? item.Description ?? string.Empty, item.SearchKeywords ?? string.Empty);

    /// <summary>
    /// Without startup integration or a tray, the General page holds only the display language, so its row uses the
    /// language card's description (iOS and Android).
    /// </summary>
    private static string? PlatformDescription(WinoPage page) => page switch
    {
        WinoPage.AppPreferencesPage when !_capabilities.StartupIntegration && !_capabilities.Tray => Translator.SettingsLanguage_Description,
        _ => null
    };
}
