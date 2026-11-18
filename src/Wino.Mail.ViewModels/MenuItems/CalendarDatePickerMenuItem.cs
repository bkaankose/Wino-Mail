using System.Collections.Generic;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Translations;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.MenuItems;

/// <summary>
/// Hosts the calendar pane's date picker. Carries the calendar mode view model so the
/// template can bind the picker to the visible range without shell involvement.
/// </summary>
public sealed partial class CalendarDatePickerMenuItem : MenuItemBase<ICalendarShellClient>
{
    private readonly IPreferencesService _preferencesService;
    private bool _isCalendarExpanded;

    public bool IsCalendarExpanded
    {
        get => _isCalendarExpanded;
        set
        {
            if (EqualityComparer<bool>.Default.Equals(_isCalendarExpanded, value))
                return;

            OnPropertyChanging();
            _isCalendarExpanded = value;
            OnIsCalendarExpandedChanged(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExpansionAutomationName));
            OnPropertyChanged(nameof(ExpansionToolTip));
            OnPropertyChanged(nameof(ExpansionGlyph));
        }
    }

    public string ExpansionAutomationName => IsCalendarExpanded
        ? Translator.CalendarPane_CollapseDatePicker
        : Translator.CalendarPane_ExpandDatePicker;

    public string ExpansionToolTip => ExpansionAutomationName;
    public string ExpansionGlyph => IsCalendarExpanded ? WinoIconGlyphs.GetGlyph(WinoIconGlyph.PanelLeftContract) : WinoIconGlyphs.GetGlyph(WinoIconGlyph.PanelLeftExpand);

    public CalendarDatePickerMenuItem(ICalendarShellClient client, IPreferencesService preferencesService)
        : base(client, null)
    {
        _preferencesService = preferencesService;
        IsCalendarExpanded = preferencesService.IsCalendarDatePickerExpanded;
    }

    private void OnIsCalendarExpandedChanged(bool value)
        => _preferencesService.IsCalendarDatePickerExpanded = value;
}
