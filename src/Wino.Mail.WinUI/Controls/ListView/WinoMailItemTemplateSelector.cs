using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI.Selectors;
using global::Wino.Mail.Controls.Core;
using Wino.Mail.WinUI;

namespace Wino.Mail.WinUI.Controls.ListView;

public partial class WinoMailItemTemplateSelector : DataTemplateSelector, IReleasableTemplateSelector
{
    // Shared by every selector. A selector must never subscribe to the preferences service on its
    // own: the service is a singleton, so each page's selector and its templates stayed reachable
    // from the event for the rest of the session.
    private static IPreferencesService? s_preferencesService;
    private static MailListDisplayMode? s_displayMode;

    public DataTemplate? SingleMailItemTemplate { get; set; }
    public DataTemplate? CompactSingleMailItemTemplate { get; set; }
    public DataTemplate? MediumSingleMailItemTemplate { get; set; }
    public DataTemplate? SpaciousSingleMailItemTemplate { get; set; }
    public DataTemplate? ThreadMailItemTemplate { get; set; }
    public DataTemplate? CompactThreadMailItemTemplate { get; set; }
    public DataTemplate? MediumThreadMailItemTemplate { get; set; }
    public DataTemplate? SpaciousThreadMailItemTemplate { get; set; }
    public DataTemplate? CalendarMailItemTemplate { get; set; }

    private bool _isReleased;

    public void ReleaseTemplates()
    {
        _isReleased = true;
        SingleMailItemTemplate = null;
        CompactSingleMailItemTemplate = null;
        MediumSingleMailItemTemplate = null;
        SpaciousSingleMailItemTemplate = null;
        ThreadMailItemTemplate = null;
        CompactThreadMailItemTemplate = null;
        MediumThreadMailItemTemplate = null;
        SpaciousThreadMailItemTemplate = null;
        CalendarMailItemTemplate = null;
    }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
        // The owning page is going away. Rows that are still being realized get no template.
        if (_isReleased)
            return base.SelectTemplateCore(item, container);

        if (item is global::Wino.Mail.Controls.Core.MailListRow row)
        {
            if (row.IsThreadHead)
            {
                return GetThreadMailTemplate() ?? throw new Exception("Missing template for thread heads.");
            }

            if (row.SourceItem is MailItemViewModel { MailCopy.ItemType: not MailItemType.Mail } &&
                CalendarMailItemTemplate != null)
            {
                return CalendarMailItemTemplate;
            }

            return GetSingleMailTemplate() ?? throw new Exception("Missing template for mail rows.");
        }

        if (item is MailItemViewModel mailItemViewModel)
        {
            // Check if it's a calendar-related item
            if (mailItemViewModel.MailCopy.ItemType != MailItemType.Mail && CalendarMailItemTemplate != null)
                return CalendarMailItemTemplate;

            return GetSingleMailTemplate() ?? throw new Exception($"Missing template for single mail items.");
        }
        else if (item is ThreadMailItemViewModel)
            return GetThreadMailTemplate() ?? throw new Exception($"Missing template for thread mail items.");

        return base.SelectTemplateCore(item, container);
    }

    private DataTemplate? GetSingleMailTemplate()
        => GetDisplayMode() switch
        {
            MailListDisplayMode.Compact => CompactSingleMailItemTemplate ?? SingleMailItemTemplate,
            MailListDisplayMode.Medium => MediumSingleMailItemTemplate ?? SingleMailItemTemplate,
            MailListDisplayMode.Spacious => SpaciousSingleMailItemTemplate ?? SingleMailItemTemplate,
            _ => SingleMailItemTemplate
        };

    private DataTemplate? GetThreadMailTemplate()
        => GetDisplayMode() switch
        {
            MailListDisplayMode.Compact => CompactThreadMailItemTemplate ?? ThreadMailItemTemplate,
            MailListDisplayMode.Medium => MediumThreadMailItemTemplate ?? ThreadMailItemTemplate,
            MailListDisplayMode.Spacious => SpaciousThreadMailItemTemplate ?? ThreadMailItemTemplate,
            _ => ThreadMailItemTemplate
        };

    /// <summary>
    /// Resolves the display mode once. Template selection runs for every realized row, so the
    /// preference is not re-read there; a change reloads the list anyway.
    /// </summary>
    private static MailListDisplayMode GetDisplayMode()
    {
        if (s_displayMode is { } cachedDisplayMode)
        {
            return cachedDisplayMode;
        }

        if (s_preferencesService is null)
        {
            s_preferencesService = WinoApplication.Current.Services.GetService<IPreferencesService>();
            if (s_preferencesService is not null)
            {
                s_preferencesService.PreferenceChanged += OnPreferenceChanged;
            }
        }

        var displayMode = s_preferencesService?.MailItemDisplayMode ?? MailListDisplayMode.Spacious;
        s_displayMode = displayMode;

        return displayMode;
    }

    private static void OnPreferenceChanged(object? sender, string propertyName)
    {
        if (propertyName == nameof(IPreferencesService.MailItemDisplayMode))
        {
            s_displayMode = null;
        }
    }
}
