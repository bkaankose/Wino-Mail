using AppKit;
using Foundation;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Calendar;

/// <summary>
/// The custom recurrence editor (Windows CalendarEventComposePage repeat card): "Repeat every"
/// interval and frequency, weekday toggles for weekly rules, and the "Ends" choice (no end date or
/// a date). Everything binds to the shared CalendarEventComposePageViewModel.
/// </summary>
public sealed partial class CalendarEventComposePageViewController
{
    private WinoSurfaceView _customRecurrence = null!;
    private NSTextField _interval = null!;
    private NSStepper _intervalStepper = null!;
    private NSPopUpButton _frequency = null!;
    private NSStackView _weekdayRow = null!;
    private NSSegmentedControl _weekdays = null!;
    private NSStackView _recurrenceEndRow = null!;
    private NSPopUpButton _recurrenceEndMode = null!;
    private NSDatePicker _recurrenceEndDate = null!;
    private BindingScope? _weekdayBindings;
    private bool _applyingRecurrence;

    private NSView BuildCustomRecurrence()
    {
        var everyLabel = WinoStyle.Label(Translator.CalendarEventCompose_RepeatEvery, WinoStyle.Body);
        var formatter = new NSNumberFormatter { NumberStyle = NSNumberFormatterStyle.None, AllowsFloats = false, Minimum = NSNumber.FromInt32(1), Maximum = NSNumber.FromInt32(99) };
        _interval = new NSTextField { Formatter = formatter, Alignment = NSTextAlignment.Right, TranslatesAutoresizingMaskIntoConstraints = false };
        _interval.WidthAnchor.ConstraintEqualTo(44).Active = true;
        WinoAccessibility.Label(_interval, Translator.CalendarEventCompose_RepeatEvery);
        _interval.Activated += (_, _) => PushInterval();
        _interval.EditingEnded += (_, _) => PushInterval();
        _intervalStepper = new NSStepper { MinValue = 1, MaxValue = 99, Increment = 1, ValueWraps = false, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoAccessibility.Label(_intervalStepper, Translator.CalendarEventCompose_RepeatEvery);
        _intervalStepper.Activated += (_, _) =>
        {
            if (_applyingRecurrence) return;
            ViewModel.SelectedRecurrenceInterval = Math.Clamp((int)_intervalStepper.IntValue, 1, 99);
        };
        _frequency = Popup(Translator.CalendarEventCompose_RepeatEvery);
        _frequency.Activated += (_, _) => Select(ViewModel.RecurrenceFrequencyOptions, _frequency, option => ViewModel.SelectedRecurrenceFrequencyOption = option);
        var everyRow = WinoLayout.HStack(8, everyLabel, _interval, _intervalStepper, _frequency);

        var onLabel = WinoStyle.Label(Translator.CalendarEventCompose_RepeatOn, WinoStyle.Body);
        _weekdays = new NSSegmentedControl { TrackingMode = NSSegmentSwitchTracking.SelectAny, SegmentCount = ViewModel.WeekdayOptions.Count, TranslatesAutoresizingMaskIntoConstraints = false };
        for (int index = 0; index < ViewModel.WeekdayOptions.Count; index++)
        {
            var option = ViewModel.WeekdayOptions[index];
            _weekdays.SetLabel(option.Label, index);
            _weekdays.SetToolTip(option.FullDayName, index);
            _weekdays.SetWidth(36, index);
        }
        WinoAccessibility.Label(_weekdays, Translator.CalendarEventCompose_RepeatOn);
        _weekdays.Activated += (_, _) =>
        {
            if (_applyingRecurrence) return;
            for (int index = 0; index < ViewModel.WeekdayOptions.Count; index++)
                ViewModel.WeekdayOptions[index].IsSelected = _weekdays.IsSelectedForSegment(index);
        };
        _weekdayRow = WinoLayout.HStack(8, onLabel, _weekdays);

        var column = WinoLayout.VStack(12, everyRow, _weekdayRow);
        column.Alignment = NSLayoutAttribute.Leading;
        column.DetachesHiddenViews = true;
        column.EdgeInsets = new NSEdgeInsets(12, 16, 12, 16);
        _customRecurrence = new WinoSurfaceView { Fill = WinoStyle.SubtleFill, Stroke = WinoStyle.GroupStroke, CornerRadius = 6, Hidden = true };
        WinoLayout.Fill(column, _customRecurrence);
        return _customRecurrence;
    }

    private NSView BuildRecurrenceEnd()
    {
        var endsLabel = WinoStyle.Label(Translator.CalendarEventCompose_RepeatEnds, WinoStyle.Body);
        _recurrenceEndMode = Popup(Translator.CalendarEventCompose_ClearRecurrenceEndDate);
        _recurrenceEndMode.AddItem(Translator.CalendarEventCompose_NoEndDate);
        _recurrenceEndMode.AddItem(Translator.CalendarMac_RecurrenceEndsOnDate);
        WinoAccessibility.Label(_recurrenceEndMode, Translator.CalendarEventCompose_RepeatEnds);
        _recurrenceEndMode.Activated += (_, _) =>
        {
            if (_applyingRecurrence) return;
            if (_recurrenceEndMode.IndexOfSelectedItem == 0)
            {
                ViewModel.ClearRecurrenceEndDateCommand.Execute(null);
            }
            else if (ViewModel.RecurrenceEndDate is null)
            {
                // Apple Calendar's default; an end on the start date would make a one-occurrence series.
                var end = ViewModel.StartDate.Date.AddMonths(1);
                ViewModel.RecurrenceEndDate = new DateTimeOffset(end, TimeZoneInfo.Local.GetUtcOffset(end));
            }
            ApplyRecurrenceEnd();
        };
        _recurrenceEndDate = DatePicker(NSDatePickerElementFlags.YearMonthDateDay);
        WinoAccessibility.Label(_recurrenceEndDate, Translator.CalendarEventCompose_RepeatEnds);
        _recurrenceEndDate.Activated += (_, _) =>
        {
            if (_applyingRecurrence) return;
            var date = FromNSDate(_recurrenceEndDate.DateValue).Date;
            if (ViewModel.RecurrenceEndDate?.Date != date) ViewModel.RecurrenceEndDate = new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));
        };
        _recurrenceEndRow = WinoLayout.HStack(8, endsLabel, _recurrenceEndMode, _recurrenceEndDate);
        _recurrenceEndRow.DetachesHiddenViews = true;
        _recurrenceEndRow.Hidden = true;
        return _recurrenceEndRow;
    }

    private void BindRecurrence()
    {
        Bind(nameof(ViewModel.IsCustomRecurrence), vm => vm.IsCustomRecurrence, custom => _customRecurrence.Hidden = !custom);
        Bind(nameof(ViewModel.IsWeeklyCustomRecurrence), vm => vm.IsWeeklyCustomRecurrence, weekly => _weekdayRow.Hidden = !weekly);
        Bind(nameof(ViewModel.IsRecurring), vm => vm.IsRecurring, recurring => _recurrenceEndRow.Hidden = !recurring);
        Bind(nameof(ViewModel.SelectedRecurrenceInterval), vm => vm.SelectedRecurrenceInterval, ApplyInterval);
        Bind(nameof(ViewModel.SelectedRecurrenceFrequencyOption), vm => vm.SelectedRecurrenceFrequencyOption, _ => ApplyFrequency());
        Bind(nameof(ViewModel.RecurrenceEndDate), vm => vm.RecurrenceEndDate, _ => ApplyRecurrenceEnd());
        // The end date can't be before the event starts; the minimum follows the start date.
        Bind(nameof(ViewModel.StartDate), vm => vm.StartDate, start => _recurrenceEndDate.MinDate = ToNSDate(start.Date));

        _weekdayBindings?.Dispose();
        _weekdayBindings = Bindings.Own(new BindingScope());
        foreach (var option in ViewModel.WeekdayOptions)
            _weekdayBindings.Own(new PropertyBinding<CalendarComposeWeekdayOption, bool>(option, nameof(option.IsSelected), o => o.IsSelected, _ => ApplyWeekdays(), Dispatcher, ReportError));
    }

    /// <summary>Frequency titles follow the interval ("week" / "weeks").</summary>
    private void FillFrequencies()
    {
        _frequency.RemoveAllItems();
        foreach (var option in ViewModel.RecurrenceFrequencyOptions) _frequency.AddItem(option.PluralLabel(ViewModel.SelectedRecurrenceInterval));
        ApplyFrequency();
    }

    private void ApplyInterval(int interval)
    {
        _applyingRecurrence = true;
        try
        {
            _interval.IntValue = interval;
            _intervalStepper.IntValue = interval;
            for (int index = 0; index < ViewModel.RecurrenceFrequencyOptions.Count && index < _frequency.ItemCount; index++)
                _frequency.ItemAtIndex(index)!.Title = ViewModel.RecurrenceFrequencyOptions[index].PluralLabel(interval);
            ApplyFrequency();
        }
        finally { _applyingRecurrence = false; }
    }

    private void PushInterval()
    {
        if (_applyingRecurrence) return;
        if (int.TryParse(_interval.StringValue, out var value) && value is >= 1 and <= 99)
        {
            if (ViewModel.SelectedRecurrenceInterval != value) ViewModel.SelectedRecurrenceInterval = value;
        }
        else
        {
            ApplyInterval(ViewModel.SelectedRecurrenceInterval);
        }
    }

    private void ApplyFrequency()
    {
        int index = ViewModel.SelectedRecurrenceFrequencyOption is { } selected ? ViewModel.RecurrenceFrequencyOptions.IndexOf(selected) : -1;
        if (index >= 0 && index < _frequency.ItemCount) _frequency.SelectItem(index);
    }

    private void ApplyWeekdays()
    {
        _applyingRecurrence = true;
        try
        {
            for (int index = 0; index < ViewModel.WeekdayOptions.Count && index < _weekdays.SegmentCount; index++)
                _weekdays.SetSelected(ViewModel.WeekdayOptions[index].IsSelected, index);
        }
        finally { _applyingRecurrence = false; }
    }

    private void ApplyRecurrenceEnd()
    {
        _applyingRecurrence = true;
        try
        {
            var end = ViewModel.RecurrenceEndDate;
            _recurrenceEndMode.SelectItem(end is null ? 0 : 1);
            _recurrenceEndDate.Hidden = end is null;
            if (end is { } value) _recurrenceEndDate.DateValue = ToNSDate(value.Date);
        }
        finally { _applyingRecurrence = false; }
    }
}
