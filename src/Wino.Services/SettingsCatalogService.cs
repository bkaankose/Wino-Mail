#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Settings;

namespace Wino.Services;

public sealed class SettingsCatalogService(IPlatformCapabilities capabilities, IWinoAccountIntelligenceSnapshotService entitlement) : ISettingsCatalog
{
    public IPlatformCapabilities Capabilities { get; } = capabilities;

    public bool CanAccessIntelligence => entitlement.CurrentEntitlement.CanAccessSurfaces;

    public bool IsVisible(WinoPage page)
        => page is not (WinoPage.SettingOptionsPage or WinoPage.SettingsPage)
            && SettingsPageAvailability.IsAvailable(page, Capabilities);

    public List<SettingsCatalogEntry> GetEntries()
    {
        var entries = new List<SettingsCatalogEntry>();
        foreach (var section in GetSections())
        {
            if (section.Title is { } title) entries.Add(new SettingsCatalogEntry(title, null, WinoIconGlyph.None));
            entries.AddRange(section.Entries);
        }
        return entries;
    }

    public List<SettingsCatalogSection> GetSections()
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

    public List<SettingsCatalogEntry> Search(string query)
        => SettingsNavigationInfoProvider.Search(query ?? string.Empty)
            .Where(item => item.PageType is { } page && IsVisible(page))
            .Select(item => Entry(item, item.PageType ?? SettingsCatalog.DefaultPage))
            .ToList();

    public string RootDescription(WinoPage rootPage)
    {
        if (PlatformDescription(rootPage) is { } platform) return platform;
        try { return SettingsNavigationInfoProvider.GetInfo(rootPage).Description ?? string.Empty; }
        catch (InvalidOperationException) { return string.Empty; }
    }

    public string Description(WinoPage page)
    {
        if (page == WinoPage.WinoIntelligenceManagementPage) return Translator.SemanticIndex_PageDescription;
        if (page == WinoPage.IntelligenceCoveragePage) return Translator.SemanticIndex_CoverageEditorPageDescription;
        if (page == WinoPage.RemoteContentRulesPage) return Translator.RemoteContentRules_Description;
        if (page == WinoPage.VoiceBrainPage) return Translator.VoiceBrain_Description;
        if (page == WinoPage.VoiceControlSettingsPage) return Translator.VoiceControlSettings_Description;
        return SettingsCatalog.RootPage(page) == page ? RootDescription(page) : string.Empty;
    }

    public string BreadcrumbDescription(WinoPage page)
        => Description(page) is { Length: > 0 } own ? own : RootDescription(SettingsCatalog.RootPage(page));

    private SettingsCatalogEntry Entry(SettingsNavigationItemInfo item, WinoPage page)
        => new(item.Title, page, item.Icon, PlatformDescription(page) ?? item.Description ?? string.Empty, item.SearchKeywords ?? string.Empty);

    /// <summary>
    /// Without startup integration or a tray, the General page holds only the display language, so its row uses the
    /// language card's description (iOS and Android).
    /// </summary>
    private string? PlatformDescription(WinoPage page) => page switch
    {
        WinoPage.AppPreferencesPage when !Capabilities.StartupIntegration && !Capabilities.Tray => Translator.SettingsLanguage_Description,
        _ => null
    };
}
