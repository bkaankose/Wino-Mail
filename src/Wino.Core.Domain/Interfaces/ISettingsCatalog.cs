#nullable enable
using System.Collections.Generic;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Settings;

namespace Wino.Core.Domain.Interfaces;

/// <summary>
/// The Settings page list for the native heads (the Mac sidebar, the iOS and Android root lists). Groups, order,
/// titles and glyphs come from <see cref="SettingsNavigationInfoProvider"/>, the source of the Windows settings pane, so
/// every platform lists the same pages in the same places; visibility follows <see cref="SettingsPageAvailability"/>
/// with the container's <see cref="IPlatformCapabilities"/> and the Wino Intelligence entitlement. The pure page
/// lookups (title, root page, glyph, group) are on the static <see cref="SettingsCatalog"/>.
/// </summary>
public interface ISettingsCatalog
{
    IPlatformCapabilities Capabilities { get; }

    /// <summary>Read from the current entitlement whenever it is asked.</summary>
    bool CanAccessIntelligence { get; }

    /// <summary>The Windows home grid is replaced by the list itself; every other page follows the shared availability rule.</summary>
    bool IsVisible(WinoPage page);

    /// <summary>The list in the Windows pane order, with a header row before each titled group (the Mac sidebar shape).</summary>
    List<SettingsCatalogEntry> GetEntries();

    /// <summary>The list in the Windows pane order: the untitled account rows first, then each titled group. Empty groups are dropped.</summary>
    List<SettingsCatalogSection> GetSections();

    /// <summary>Search results in the shared ranking (title, keywords, description), without headers.</summary>
    List<SettingsCatalogEntry> Search(string query);

    /// <summary>The description of a list row: the Windows text, or the platform's wording where Windows names settings it lacks.</summary>
    string RootDescription(WinoPage rootPage);

    /// <summary>The Windows page description shown under a page title; sub-pages (account pages, editors) have none of their own.</summary>
    string Description(WinoPage page);

    /// <summary>The description under a breadcrumb title: the page's own, otherwise its list row's.</summary>
    string BreadcrumbDescription(WinoPage page);
}
