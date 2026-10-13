using System.Globalization;
using UIKit;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain;
using Wino.Mail.UIKit.Views.Mail.Compose;
using Wino.Presentation.Bindings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Calendar;

/// <summary>
/// The custom recurrence editor (Mac Recurrence partial, Windows CalendarEventComposePage repeat card): "Repeat every"
/// interval and frequency, weekday toggles for weekly rules, and the "Ends" choice (no end date or a date). Everything
/// binds to the shared CalendarEventComposePageViewModel.
/// </summary>
public sealed partial class CalendarEventComposePageViewController
{
    private readonly WinoSurfaceView _customRecurrence = new() { Fill = WinoStyle.SubtleFill, Stroke = WinoStyle.GroupStroke, CornerRadius = 6, Hidden = true };
    private readonly UILabel _intervalLabel = WinoStyle.Label("1", WinoStyle.BodyMedium);
    private readonly UIStepper _intervalStepper = new() { MinimumValue = 1, MaximumValue = 99, StepValue = 1, Wraps = false, AutoRepeat = true, TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UIButton _frequencyButton = MenuButton(Translator.CalendarEventCompose_RepeatEvery);
    private readonly UIStackView _weekdayRow = WinoLayout.HStack(3);
    private readonly List<UIButton> _weekdayButtons = [];
    private UIView? _weekdayBlock;
    private readonly UIButton _recurrenceEndMode = MenuButton(Translator.CalendarEventCompose_RepeatEnds);
    private readonly UIDatePicker _recurrenceEndDate = DatePicker(UIDatePickerMode.Date, Translator.CalendarEventCompose_RepeatEnds);
    private UIView? _recurrenceEndRow;
    private bool _applyingRecurrence;

    private UIView BuildCustomRecurrence()
    {
        var everyLabel = WinoStyle.Label(Translator.CalendarEventCompose_RepeatEvery, WinoStyle.Body);
        _intervalLabel.TextAlignment = UITextAlignment.Center;
        _intervalLabel.IsAccessibilityElement = false;
        _intervalLabel.WidthAnchor.ConstraintGreaterThanOrEqualTo(28).Active = true;
        WinoAccessibility.Label(_intervalStepper, Translator.CalendarEventCompose_RepeatEvery);
        _intervalStepper.ValueChanged += (_, _) =>
        {
            if (_applyingRecurrence) return;
            ViewModel.SelectedRecurrenceInterval = Math.Clamp((int)_intervalStepper.Value, 1, 99);
        };
        _frequencyButton.Menu = DeferredMenu(() => OptionMenu(ViewModel.RecurrenceFrequencyOptions, ViewModel.SelectedRecurrenceFrequencyOption,
            option => option.PluralLabel(ViewModel.SelectedRecurrenceInterval), option => ViewModel.SelectedRecurrenceFrequencyOption = option));
        // The label above its controls: one line would not fit the narrowest iPhone form column.
        var everyRow = WinoLayout.VStack(6, everyLabel, Leading(WinoLayout.HStack(8, _intervalLabel, _intervalStepper, _frequencyButton)));
        everyRow.Alignment = UIStackViewAlignment.Fill;

        var onLabel = WinoStyle.Label(Translator.CalendarEventCompose_RepeatOn, WinoStyle.Body);
        foreach (var option in ViewModel.WeekdayOptions)
        {
            var captured = option;
            var button = new UIButton { TranslatesAutoresizingMaskIntoConstraints = false, AccessibilityLabel = option.FullDayName };
            // Seven fit the narrowest iPhone form column.
            WinoLayout.Size(button, 34, 34);
            button.PrimaryActionTriggered += (_, _) =>
            {
                if (_applyingRecurrence) return;
                captured.IsSelected = !captured.IsSelected;
                MarkEdited();
            };
            _weekdayButtons.Add(button);
            _weekdayRow.AddArrangedSubview(button);
        }
        _weekdayRow.AccessibilityLabel = Translator.CalendarEventCompose_RepeatOn;
        var weekdayBlock = WinoLayout.VStack(6, onLabel, _weekdayRow);
        _weekdayBlock = weekdayBlock;
        ApplyWeekdays();

        var column = WinoLayout.VStack(12, everyRow, weekdayBlock);
        column.Alignment = UIStackViewAlignment.Fill;
        WinoLayout.Insets(column, 12, 12, 12, 12);
        WinoLayout.Fill(column, _customRecurrence);
        return _customRecurrence;
    }

    private UIView BuildRecurrenceEnd()
    {
        var endsLabel = WinoStyle.Label(Translator.CalendarEventCompose_RepeatEnds, WinoStyle.Body);
        _recurrenceEndMode.Menu = DeferredMenu(() =>
        [
            EditorFormatToolbar.Action(Translator.CalendarEventCompose_NoEndDate, () => SetRecurrenceEnd(false), ViewModel.RecurrenceEndDate is null),
            EditorFormatToolbar.Action(Translator.CalendarMac_RecurrenceEndsOnDate, () => SetRecurrenceEnd(true), ViewModel.RecurrenceEndDate is not null)
        ]);
        _recurrenceEndDate.ValueChanged += (_, _) =>
        {
            if (_applyingRecurrence) return;
            var date = FromNSDate(_recurrenceEndDate.Date).Date;
            if (ViewModel.RecurrenceEndDate?.Date != date) ViewModel.RecurrenceEndDate = new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));
        };
        var row = Leading(WinoLayout.HStack(8, endsLabel, _recurrenceEndMode, _recurrenceEndDate));
        row.Hidden = true;
        _recurrenceEndRow = row;
        return row;
    }

    private void SetRecurrenceEnd(bool hasEnd)
    {
        if (!hasEnd)
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
    }

    private void BindRecurrence()
    {
        Bind(nameof(ViewModel.IsCustomRecurrence), vm => vm.IsCustomRecurrence, custom => _customRecurrence.Hidden = !custom);
        Bind(nameof(ViewModel.IsWeeklyCustomRecurrence), vm => vm.IsWeeklyCustomRecurrence, weekly => { if (_weekdayBlock is { } block) block.Hidden = !weekly; });
        Bind(nameof(ViewModel.IsRecurring), vm => vm.IsRecurring, recurring => { if (_recurrenceEndRow is { } row) row.Hidden = !recurring; });
        Bind(nameof(ViewModel.SelectedRecurrenceInterval), vm => vm.SelectedRecurrenceInterval, ApplyInterval);
        Bind(nameof(ViewModel.SelectedRecurrenceFrequencyOption), vm => vm.SelectedRecurrenceFrequencyOption, _ => ApplyFrequency());
        Bind(nameof(ViewModel.RecurrenceEndDate), vm => vm.RecurrenceEndDate, _ => ApplyRecurrenceEnd());
        // The end date can't be before the event starts; the minimum follows the start date.
        Bind(nameof(ViewModel.StartDate), vm => vm.StartDate, start => _recurrenceEndDate.MinimumDate = ToNSDate(start.Date));

        foreach (var option in ViewModel.WeekdayOptions)
            Bindings.Own(new PropertyBinding<CalendarComposeWeekdayOption, bool>(option, nameof(option.IsSelected), o => o.IsSelected, _ => ApplyWeekdays(), Dispatcher, ReportError));
    }

    private void ApplyInterval(int interval)
    {
        _applyingRecurrence = true;
        try
        {
            _intervalLabel.Text = interval.ToString(CultureInfo.CurrentCulture);
            _intervalStepper.Value = interval;
            _intervalStepper.AccessibilityValue = interval.ToString(CultureInfo.CurrentCulture);
            // Frequency titles follow the interval ("week" / "weeks").
            ApplyFrequency();
        }
        finally { _applyingRecurrence = false; }
    }

    private void ApplyFrequency()
        => SetMenuTitle(_frequencyButton, ViewModel.SelectedRecurrenceFrequencyOption?.PluralLabel(ViewModel.SelectedRecurrenceInterval));

    private void ApplyWeekdays()
    {
        _applyingRecurrence = true;
        try
        {
            for (int index = 0; index < ViewModel.WeekdayOptions.Count && index < _weekdayButtons.Count; index++)
            {
                var option = ViewModel.WeekdayOptions[index];
                var button = _weekdayButtons[index];
                var configuration = option.IsSelected ? UIButtonConfiguration.FilledButtonConfiguration : UIButtonConfiguration.GrayButtonConfiguration;
                configuration.Title = option.Label;
                configuration.CornerStyle = UIButtonConfigurationCornerStyle.Capsule;
                configuration.ContentInsets = new NSDirectionalEdgeInsets(0, 0, 0, 0);
                if (option.IsSelected) configuration.BaseBackgroundColor = WinoStyle.Accent;
                button.Configuration = configuration;
                button.AccessibilityTraits = option.IsSelected ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
            }
        }
        finally { _applyingRecurrence = false; }
    }

    private void ApplyRecurrenceEnd()
    {
        _applyingRecurrence = true;
        try
        {
            var end = ViewModel.RecurrenceEndDate;
            SetMenuTitle(_recurrenceEndMode, end is null ? Translator.CalendarEventCompose_NoEndDate : Translator.CalendarMac_RecurrenceEndsOnDate);
            _recurrenceEndDate.Hidden = end is null;
            if (end is { } value) _recurrenceEndDate.Date = ToNSDate(value.Date);
        }
        finally { _applyingRecurrence = false; }
    }
}
