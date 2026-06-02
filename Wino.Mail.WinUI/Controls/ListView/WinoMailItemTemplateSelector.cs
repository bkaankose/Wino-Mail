using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.WinUI;

namespace Wino.Mail.WinUI.Controls.ListView;

public partial class WinoMailItemTemplateSelector : DataTemplateSelector
{
    private IPreferencesService? _preferencesService;

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

    private MailListDisplayMode GetDisplayMode()
    {
        _preferencesService ??= WinoApplication.Current.Services.GetService<IPreferencesService>();
        return _preferencesService?.MailItemDisplayMode ?? MailListDisplayMode.Spacious;
    }
}
