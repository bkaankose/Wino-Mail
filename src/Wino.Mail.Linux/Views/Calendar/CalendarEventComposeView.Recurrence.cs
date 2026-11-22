using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.ViewModels.Calendar;
using Wino.Presentation.Bindings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Calendar;

/// <summary>
/// The Repeat row (Windows repeat card, macOS composer recurrence part): the presets, the custom rule (interval, unit,
/// weekday toggles for a weekly rule), the end of the series (none or a date), the summary and the two notices for an
/// occurrence and for a rule the options cannot show. It offers exactly what the shared ViewModel has.
/// </summary>
internal sealed partial class CalendarEventComposeView
{
    private readonly OptionDropDown<CalendarComposeRepeatOption> _repeat = new(Translator.CalendarEventCompose_Repeat);
    private readonly Gtk.Box _custom = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
    private readonly Gtk.SpinButton _interval = Gtk.SpinButton.NewWithRange(CalendarEventComposePageViewModel.MinRecurrenceInterval, CalendarEventComposePageViewModel.MaxRecurrenceInterval, 1);
    private readonly OptionDropDown<CalendarComposeFrequencyOption> _frequency = new(Translator.CalendarEventCompose_RepeatEvery);
    private readonly Gtk.Box _weekdayRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
    private readonly Gtk.Box _weekdays = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
    private readonly Gtk.Box _endRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
    private readonly Gtk.DropDown _endMode = Gtk.DropDown.NewFromStrings([Translator.CalendarEventCompose_NoEndDate, Translator.CalendarMac_RecurrenceEndsOnDate]);
    private DateField? _endDateField;
    private readonly Gtk.Label _occurrenceInfo = Gtk.Label.New(Translator.CalendarEventCompose_OccurrenceRepeatInfo);
    private readonly Gtk.Label _preserved = Gtk.Label.New(Translator.CalendarEventCompose_RecurrencePreserved);
    private readonly Gtk.Label _summary = Gtk.Label.New(string.Empty);
    private readonly List<(CalendarComposeWeekdayOption Option, Gtk.ToggleButton Toggle)> _weekdayToggles = [];
    private BindingScope? _recurrenceOptionsScope;
    private BindingScope? _weekdayScope;
    private bool _applyingRecurrence;

    partial void BuildRecurrence(Gtk.Box host)
    {
        var column = Gtk.Box.New(Gtk.Orientation.Vertical, 8);
        foreach (var label in (Gtk.Label[])[_occurrenceInfo, _preserved, _summary])
        {
            label.SetXalign(0);
            label.SetWrap(true);
            label.AddCssClass("caption");
        }
        _summary.AddCssClass("dim-label");

        var every = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        every.Append(Gtk.Label.New(Translator.CalendarEventCompose_RepeatEvery));
        _interval.SetNumeric(true);
        _interval.SetWidthChars(3);
        WinoA11y.Label(_interval, Translator.CalendarEventCompose_RepeatEvery);
        every.Append(_interval);
        every.Append(_frequency.Root);
        _weekdays.AddCssClass("linked");
        _weekdayRow.Append(Gtk.Label.New(Translator.CalendarEventCompose_RepeatOn));
        _weekdayRow.Append(_weekdays);
        _custom.AddCssClass("card");
        foreach (var part in (Gtk.Widget[])[every, _weekdayRow])
        {
            part.SetMarginStart(16);
            part.SetMarginEnd(16);
            _custom.Append(part);
        }
        every.SetMarginTop(12);
        _weekdayRow.SetMarginBottom(12);

        _endDateField = new DateField(Translator.CalendarEventCompose_RepeatEnds);
        WinoA11y.Rename(_endMode, Translator.CalendarEventCompose_RepeatEnds);
        _endRow.Append(Gtk.Label.New(Translator.CalendarEventCompose_RepeatEnds));
        _endRow.Append(_endMode);
        _endRow.Append(_endDateField.Root);

        _repeat.Root.SetHalign(Gtk.Align.Start);
        foreach (var part in (Gtk.Widget[])[_occurrenceInfo, _repeat.Root, _custom, _endRow, _preserved, _summary]) column.Append(part);
        host.Append(Row(WinoIconGlyph.CalendarEventRepeat, column));

        _repeat.Changed += option => ViewModel.SelectedRepeatOption = option;
        _frequency.Changed += option => ViewModel.SelectedRecurrenceFrequencyOption = option;
        Bindings.Own(GtkSignals.Connect<Gtk.SpinButton>(h => _interval.OnValueChanged += h, h => _interval.OnValueChanged -= h, (_, _) =>
        {
            if (_applyingRecurrence) return;
            ViewModel.SelectedRecurrenceInterval = Math.Clamp(_interval.GetValueAsInt(), CalendarEventComposePageViewModel.MinRecurrenceInterval, CalendarEventComposePageViewModel.MaxRecurrenceInterval);
        }));
        Bindings.Own(GtkSignals.OnNotify(_endMode, "selected", EndModeChanged));
        _endDateField.Changed += date =>
        {
            if (_applyingRecurrence) return;
            var start = DateOnly.FromDateTime(ViewModel.StartDate.Date);
            var picked = (date < start ? start : date).ToDateTime(TimeOnly.MinValue);
            if (ViewModel.RecurrenceEndDate?.Date != picked) ViewModel.RecurrenceEndDate = new DateTimeOffset(picked, TimeZoneInfo.Local.GetUtcOffset(picked));
        };

        Bind.BindCollection(ViewModel.RepeatOptions, RefillRepeatOptions);
        Bind.Bind(nameof(CalendarEventComposePageViewModel.SelectedRepeatOption), static vm => vm.SelectedRepeatOption, option => _repeat.Selected = option);
        Bind.BindCollection(ViewModel.RecurrenceFrequencyOptions, FillFrequencies);
        Bind.Bind(nameof(CalendarEventComposePageViewModel.SelectedRecurrenceFrequencyOption), static vm => vm.SelectedRecurrenceFrequencyOption, option => _frequency.Selected = option);
        Bind.Bind(nameof(CalendarEventComposePageViewModel.SelectedRecurrenceInterval), static vm => vm.SelectedRecurrenceInterval, ApplyInterval);
        Bind.BindCollection(ViewModel.WeekdayOptions, BuildWeekdays);
        Bind.Bind(nameof(CalendarEventComposePageViewModel.IsCustomRecurrence), static vm => vm.IsCustomRecurrence, _custom.SetVisible);
        Bind.Bind(nameof(CalendarEventComposePageViewModel.IsWeeklyCustomRecurrence), static vm => vm.IsWeeklyCustomRecurrence, _weekdayRow.SetVisible);
        Bind.Bind(nameof(CalendarEventComposePageViewModel.IsRecurring), static vm => vm.IsRecurring, _endRow.SetVisible);
        Bind.Bind(nameof(CalendarEventComposePageViewModel.RecurrenceEndDate), static vm => vm.RecurrenceEndDate, _ => ApplyRecurrenceEnd());
        Bind.Bind(nameof(CalendarEventComposePageViewModel.RecurrenceSummary), static vm => vm.RecurrenceSummary, text => _summary.SetText(text ?? string.Empty));
        Bind.Bind(nameof(CalendarEventComposePageViewModel.HasPreservedRecurrence), static vm => vm.HasPreservedRecurrence, _preserved.SetVisible);
        Bind.Bind(nameof(CalendarEventComposePageViewModel.CanEditRecurrence), static vm => vm.CanEditRecurrence, can =>
        {
            _occurrenceInfo.SetVisible(!can);
            foreach (var part in (Gtk.Widget[])[_repeat.Root, _custom, _endRow]) part.SetSensitive(can);
        });
        Bindings.Own(new Wino.Presentation.Linux.ActionDisposable(() =>
        {
            _repeat.Dispose();
            _frequency.Dispose();
            _endDateField?.Dispose();
        }));
    }

    /// <summary>The preset texts follow the start date ("Every week on Tuesday"): the drop-down is refilled when one changes.</summary>
    private void RefillRepeatOptions()
    {
        _recurrenceOptionsScope?.Dispose();
        var scope = _recurrenceOptionsScope = Bindings.Own(new BindingScope());
        var refill = scope.Own(new MainLoop.Coalescer(FillRepeat));
        foreach (var option in ViewModel.RepeatOptions)
            scope.Own(new PropertyBinding<CalendarComposeRepeatOption, string>(option, nameof(CalendarComposeRepeatOption.DisplayText), static o => o.DisplayText, _ => refill.Request(), Dispatcher, ReportError));
        FillRepeat();
    }

    private void FillRepeat() => _repeat.SetOptions([.. ViewModel.RepeatOptions], static option => option.DisplayText, ViewModel.SelectedRepeatOption);

    private void FillFrequencies()
        => _frequency.SetOptions([.. ViewModel.RecurrenceFrequencyOptions], option => option.PluralLabel(ViewModel.SelectedRecurrenceInterval), ViewModel.SelectedRecurrenceFrequencyOption);

    private void ApplyInterval(int interval)
    {
        _applyingRecurrence = true;
        try { _interval.SetValue(interval); }
        finally { _applyingRecurrence = false; }
        FillFrequencies();
    }

    private void BuildWeekdays()
    {
        while (_weekdays.GetFirstChild() is { } child) _weekdays.Remove(child);
        _weekdayToggles.Clear();
        _weekdayScope?.Dispose();
        var scope = _weekdayScope = Bindings.Own(new BindingScope());
        foreach (var option in ViewModel.WeekdayOptions)
        {
            var toggle = Gtk.ToggleButton.NewWithLabel(option.Label);
            toggle.SetSizeRequest(36, -1);
            toggle.SetTooltipText(option.FullDayName);
            WinoA11y.Rename(toggle, option.FullDayName);
            var captured = option;
            scope.Own(new PropertyBinding<CalendarComposeWeekdayOption, bool>(option, nameof(CalendarComposeWeekdayOption.IsSelected), static o => o.IsSelected, selected =>
            {
                _applyingRecurrence = true;
                try { toggle.SetActive(selected); }
                finally { _applyingRecurrence = false; }
            }, Dispatcher, ReportError));
            scope.Own(GtkSignals.OnToggled(toggle, () =>
            {
                if (_applyingRecurrence) return;
                captured.IsSelected = toggle.GetActive();
                // The ViewModel keeps the last weekday selected; the toggle follows the option, not the click.
                if (toggle.GetActive() != captured.IsSelected)
                {
                    _applyingRecurrence = true;
                    try { toggle.SetActive(captured.IsSelected); }
                    finally { _applyingRecurrence = false; }
                }
            }));
            _weekdayToggles.Add((option, toggle));
            _weekdays.Append(toggle);
        }
    }

    private void EndModeChanged()
    {
        WinoA11y.Rename(_endMode, Translator.CalendarEventCompose_RepeatEnds);
        if (_applyingRecurrence) return;
        if (_endMode.GetSelected() == 0) ViewModel.ClearRecurrenceEndDateCommand.Execute(null);
        else if (ViewModel.RecurrenceEndDate is null)
        {
            // An end on the start date would make a one-occurrence series; a month later is the Mac's default.
            var end = ViewModel.StartDate.Date.AddMonths(1);
            ViewModel.RecurrenceEndDate = new DateTimeOffset(end, TimeZoneInfo.Local.GetUtcOffset(end));
        }
        ApplyRecurrenceEnd();
    }

    private void ApplyRecurrenceEnd()
    {
        _applyingRecurrence = true;
        try
        {
            var end = ViewModel.RecurrenceEndDate;
            _endMode.SetSelected(end is null ? 0u : 1u);
            if (_endDateField is { } field)
            {
                field.Root.SetVisible(end is not null);
                if (end is { } date) field.Date = DateOnly.FromDateTime(date.Date);
            }
        }
        finally { _applyingRecurrence = false; }
    }

#if DEBUG
    internal string DescribeRecurrence()
        => $"repeat='{ViewModel.SelectedRepeatOption?.DisplayText}' custom={_custom.GetVisible()} interval={ViewModel.SelectedRecurrenceInterval} frequency='{ViewModel.SelectedRecurrenceFrequencyOption?.DisplayText}' weekdays=[{string.Join(",", _weekdayToggles.Where(static t => t.Toggle.GetActive()).Select(static t => t.Option.Label))}] end={ViewModel.RecurrenceEndDate:yyyy-MM-dd} summary='{ViewModel.RecurrenceSummary}'";

    internal void DebugSetRepeat(int index)
    {
        if (index >= 0 && index < ViewModel.RepeatOptions.Count) _repeat.Root.SetSelected((uint)index);
    }

    internal void DebugSetInterval(int value) => _interval.SetValue(value);

    internal void DebugSetFrequency(int index)
    {
        if (index >= 0 && index < ViewModel.RecurrenceFrequencyOptions.Count) _frequency.Root.SetSelected((uint)index);
    }

    internal void DebugToggleWeekday(int index)
    {
        if (index >= 0 && index < _weekdayToggles.Count) _weekdayToggles[index].Toggle.SetActive(!_weekdayToggles[index].Toggle.GetActive());
    }

    internal void DebugSetEnd(DateOnly? date)
    {
        if (date is not { } value)
        {
            _endMode.SetSelected(0);
            return;
        }
        _endMode.SetSelected(1);
        _endDateField?.Pick(value);
    }
#endif
}
