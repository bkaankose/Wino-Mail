using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using global::Wino.Mail.Controls.Core;
using Wino.Mail.WinUI;

namespace Wino.Mail.WinUI.Controls.ListView;

public partial class WinoMailItemTemplateSelector : DataTemplateSelector
{
    private IPreferencesService? _preferencesService;
    private MailListDisplayMode? _displayMode;

    public DataTemplate? SingleMailItemTemplate { get; set; }
    public DataTemplate? CompactSingleMailItemTemplate { get; set; }
    public DataTemplate? MediumSingleMailItemTemplate { get; set; }
    public DataTemplate? SpaciousSingleMailItemTemplate { get; set; }
    public DataTemplate? ThreadMailItemTemplate { get; set; }
    public DataTemplate? CompactThreadMailItemTemplate { get; set; }
    public DataTemplate? MediumThreadMailItemTemplate { get; set; }
    public DataTemplate? SpaciousThreadMailItemTemplate { get; set; }
    public DataTemplate? CalendarMailItemTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
    {
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
    /// Resolves the display mode once per selector. Template selection runs for every realized
    /// row, so the preference is not re-read there; a change reloads the list anyway.
    /// </summary>
    private MailListDisplayMode GetDisplayMode()
    {
        if (_displayMode is { } cachedDisplayMode)
        {
            return cachedDisplayMode;
        }

        if (_preferencesService is null)
        {
            _preferencesService = WinoApplication.Current.Services.GetService<IPreferencesService>();
            if (_preferencesService is not null)
            {
                _preferencesService.PreferenceChanged += OnPreferenceChanged;
            }
        }

        var displayMode = _preferencesService?.MailItemDisplayMode ?? MailListDisplayMode.Spacious;
        _displayMode = displayMode;

        return displayMode;
    }

    private void OnPreferenceChanged(object? sender, string propertyName)
    {
        if (propertyName == nameof(IPreferencesService.MailItemDisplayMode))
        {
            _displayMode = null;
        }
    }
}
