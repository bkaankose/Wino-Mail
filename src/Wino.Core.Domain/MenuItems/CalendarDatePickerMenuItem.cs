using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Translations;

namespace Wino.Core.Domain.MenuItems;

/// <summary>
/// Hosts the calendar pane's date picker. Carries the calendar mode view model so the
/// template can bind the picker to the visible range without shell involvement.
/// </summary>
public sealed partial class CalendarDatePickerMenuItem : MenuItemBase<ICalendarShellClient>
{
    private readonly IPreferencesService _preferencesService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpansionAutomationName), nameof(ExpansionToolTip), nameof(ExpansionGlyph))]
    public partial bool IsCalendarExpanded { get; set; }

    public string ExpansionAutomationName => IsCalendarExpanded
        ? Translator.CalendarPane_CollapseDatePicker
        : Translator.CalendarPane_ExpandDatePicker;

    public string ExpansionToolTip => ExpansionAutomationName;
    public string ExpansionGlyph => IsCalendarExpanded ? "\uE89F" : "\uE8A0";

    public CalendarDatePickerMenuItem(ICalendarShellClient client, IPreferencesService preferencesService)
        : base(client, null)
    {
        _preferencesService = preferencesService;
        IsCalendarExpanded = preferencesService.IsCalendarDatePickerExpanded;
    }

    partial void OnIsCalendarExpandedChanged(bool value)
        => _preferencesService.IsCalendarDatePickerExpanded = value;
}
