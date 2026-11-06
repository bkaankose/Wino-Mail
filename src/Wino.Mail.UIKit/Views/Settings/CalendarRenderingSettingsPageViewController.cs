using UIKit;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Calendar rendering: first day of the week, working days and hours, hour height, time format and the day header
/// date format with live previews (Windows CalendarRenderingSettingsPage). On phone width every pop-up, picker and
/// field sits under its card text.
/// </summary>
public sealed class CalendarRenderingSettingsPageViewController(CalendarRenderingSettingsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<CalendarRenderingSettingsPageViewModel>(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;
        var days = vm.DayNames.ToList();

        var firstDay = StackedCard(Translator.CalendarSettings_FirstDayOfWeek_Header, Translator.CalendarSettings_FirstDayOfWeek_Description, WinoIconGlyph.Calendar,
            Bind.PopUp(vm, days, nameof(vm.SelectedFirstDayOfWeekIndex), s => s.SelectedFirstDayOfWeekIndex, (s, v) => s.SelectedFirstDayOfWeekIndex = v));

        bool WorkingOn(CalendarRenderingSettingsPageViewModel s) => s.IsWorkingHoursEnabled;
        var working = Expander(Translator.CalendarSettings_WorkingDays_Header, Translator.CalendarSettings_WorkingDays_Description, WinoIconGlyph.CalendarWorkWeek, null,
            Card(Translator.CalendarSettings_HighlightWorkingHours_Label, null, WinoIconGlyph.None,
                Bind.Switch(vm, nameof(vm.IsWorkingHoursEnabled), WorkingOn, (s, v) => s.IsWorkingHoursEnabled = v, Translator.CalendarSettings_HighlightWorkingHours_Label)),
            Bind.Enabled(StackedCard(Translator.CalendarSettings_WorkingDays_From, null, WinoIconGlyph.None, DayAndTime(
                    Bind.PopUp(vm, days, nameof(vm.WorkingDayStartIndex), s => s.WorkingDayStartIndex, (s, v) => s.WorkingDayStartIndex = v),
                    Bind.TimePicker(vm, nameof(vm.WorkingHourStart), s => s.WorkingHourStart, (s, v) => s.WorkingHourStart = v, Translator.CalendarSettings_WorkingDays_From))),
                vm, nameof(vm.IsWorkingHoursEnabled), WorkingOn),
            Bind.Enabled(StackedCard(Translator.CalendarSettings_WorkingDays_To, null, WinoIconGlyph.None, DayAndTime(
                    Bind.PopUp(vm, days, nameof(vm.WorkingDayEndIndex), s => s.WorkingDayEndIndex, (s, v) => s.WorkingDayEndIndex = v),
                    Bind.TimePicker(vm, nameof(vm.WorkingHourEnd), s => s.WorkingHourEnd, (s, v) => s.WorkingHourEnd = v, Translator.CalendarSettings_WorkingDays_To))),
                vm, nameof(vm.IsWorkingHoursEnabled), WorkingOn));

        // Hour height: slider plus the Windows sample cell that grows with the value.
        var slider = new UISlider { MinValue = 40, MaxValue = 120, Continuous = true, TranslatesAutoresizingMaskIntoConstraints = false };
        slider.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        WinoAccessibility.Label(slider, Translator.CalendarSettings_HourHeight_Header);
        var sample = new WinoSurfaceView { Stroke = WinoStyle.SecondaryText, StrokeWidth = 1, CornerRadius = 2, TranslatesAutoresizingMaskIntoConstraints = false };
        var sampleHeight = sample.HeightAnchor.ConstraintEqualTo(60);
        sampleHeight.Active = true;
        sample.WidthAnchor.ConstraintEqualTo(100).Active = true;
        var half = new WinoSeparator { Fill = WinoStyle.SecondaryText };
        sample.AddSubview(half);
        NSLayoutConstraint.ActivateConstraints(
        [
            half.LeadingAnchor.ConstraintEqualTo(sample.LeadingAnchor),
            half.TrailingAnchor.ConstraintEqualTo(sample.TrailingAnchor),
            half.CenterYAnchor.ConstraintEqualTo(sample.CenterYAnchor)
        ]);
        var sampleRow = Row(Caption("00:00"), sample, WinoLayout.Spacer());
        sampleRow.Alignment = UIStackViewAlignment.Bottom;
        Bind.Bind(vm, nameof(vm.CellHourHeight), s => s.CellHourHeight, value =>
        {
            slider.Value = (float)value;
            sampleHeight.Constant = (nfloat)Math.Clamp(value, 40, 120);
        });
        Bind.OnActivated(slider, () => vm.CellHourHeight = Math.Round(slider.Value / 5) * 5, UIControlEvent.ValueChanged);
        var hourHeight = Card(Translator.CalendarSettings_HourHeight_Header, Translator.CalendarSettings_HourHeight_Description);
        var hourContent = WinoLayout.VStack(12, slider, sampleRow);
        // The slider takes the card's width (a leading stack would collapse it to its thumb).
        hourContent.Alignment = UIStackViewAlignment.Fill;
        hourHeight.BottomContent = hourContent;

        var timeFormat = StackedCard(Translator.SettingsTimeFormat_Title, Translator.SettingsTimeFormat_Description, WinoIconGlyph.None, WinoLayout.VStack(WinoStyle.Space2,
            Bind.PopUp(vm, vm.TimeFormatPreferenceOptions, nameof(vm.SelectedTimeFormatPreferenceIndex), s => s.SelectedTimeFormatPreferenceIndex, (s, v) => s.SelectedTimeFormatPreferenceIndex = v),
            Bind.Label(vm, nameof(vm.TimedHourLabelPreview), s => s.TimedHourLabelPreview, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0)));

        var format = Bind.TextField(vm, nameof(vm.TimedDayHeaderDateFormat), s => s.TimedDayHeaderDateFormat, (s, v) => s.TimedDayHeaderDateFormat = v,
            Translator.CalendarSettings_TimedDayHeaderFormat_Placeholder, 0);
        format.AutocorrectionType = UITextAutocorrectionType.No;
        format.AutocapitalizationType = UITextAutocapitalizationType.None;
        format.HeightAnchor.ConstraintGreaterThanOrEqualTo((nfloat)WinoStyle.MinimumHitTarget).Active = true;
        WinoAccessibility.Label(format, Translator.CalendarSettings_TimedDayHeaderFormat_Header);
        var presets = Bind.PopUp(vm, vm.TimedDayHeaderFormatPresets.ToList(), nameof(vm.SelectedTimedDayHeaderFormatPresetIndex),
            s => s.SelectedTimedDayHeaderFormatPresetIndex, (s, v) => s.SelectedTimedDayHeaderFormatPresetIndex = v);
        var headerContent = WinoLayout.VStack(WinoStyle.Space2, format, presets,
            Bind.Label(vm, nameof(vm.TimedDayHeaderFormatPreview), s => s.TimedDayHeaderFormatPreview, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0));
        // The format field spans the card; the preset pop-up keeps its width under it.
        format.WidthAnchor.ConstraintEqualTo(headerContent.WidthAnchor).Active = true;
        var headerFormat = StackedCard(Translator.CalendarSettings_TimedDayHeaderFormat_Header, Translator.CalendarSettings_TimedDayHeaderFormat_Description, WinoIconGlyph.None,
            headerContent);

        var rendering = new WinoSettingsExpander(Translator.CalendarSettings_CalendarRendering_Header, Translator.CalendarSettings_CalendarRendering_Description, WinoIconGlyph.CalendarSettings, isExpanded: true);
        rendering.Add(hourHeight);
        rendering.Add(timeFormat);
        rendering.Add(headerFormat);

        AddGroup(null, firstDay, working, rendering);
    }

    /// <summary>The day pop-up with its time picker trailing on the same line.</summary>
    private static UIStackView DayAndTime(UIView day, UIView time)
    {
        var row = Row(day, time);
        row.Distribution = UIStackViewDistribution.Fill;
        WinoLayout.Priorities(day, UILayoutConstraintAxis.Horizontal, 1, 250);
        return row;
    }
}
