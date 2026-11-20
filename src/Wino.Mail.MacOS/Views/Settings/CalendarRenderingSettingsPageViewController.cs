using AppKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Presentation.AppKit;
using Wino.Mail.ViewModels.Calendar;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Calendar rendering: first day of the week, working days and hours, hour height, time format and
/// the day header date format with live previews (Windows CalendarRenderingSettingsPage).
/// </summary>
public sealed class CalendarRenderingSettingsPageViewController(CalendarRenderingSettingsPageViewModel viewModel, IDispatcher dispatcher, IWinoLogger logger)
    : SettingsPageViewController<CalendarRenderingSettingsPageViewModel>(viewModel, dispatcher, logger)
{
    protected override void BuildPage()
    {
        var vm = ViewModel;
        var days = vm.DayNames.ToList();

        var firstDay = Card(Translator.CalendarSettings_FirstDayOfWeek_Header, Translator.CalendarSettings_FirstDayOfWeek_Description, WinoIconGlyph.Calendar,
            Bind.PopUp(vm, days, nameof(vm.SelectedFirstDayOfWeekIndex), s => s.SelectedFirstDayOfWeekIndex, (s, v) => s.SelectedFirstDayOfWeekIndex = v));

        bool WorkingOn(CalendarRenderingSettingsPageViewModel s) => s.IsWorkingHoursEnabled;
        var working = Expander(Translator.CalendarSettings_WorkingDays_Header, Translator.CalendarSettings_WorkingDays_Description, WinoIconGlyph.CalendarWorkWeek, null,
            Card(Translator.CalendarSettings_HighlightWorkingHours_Label, null, WinoIconGlyph.None,
                Bind.Switch(vm, nameof(vm.IsWorkingHoursEnabled), WorkingOn, (s, v) => s.IsWorkingHoursEnabled = v, Translator.CalendarSettings_HighlightWorkingHours_Label)),
            Bind.Enabled(Card(Translator.CalendarSettings_WorkingDays_From, null, WinoIconGlyph.None, Row(
                    Bind.PopUp(vm, days, nameof(vm.WorkingDayStartIndex), s => s.WorkingDayStartIndex, (s, v) => s.WorkingDayStartIndex = v, 140),
                    Bind.TimePicker(vm, nameof(vm.WorkingHourStart), s => s.WorkingHourStart, (s, v) => s.WorkingHourStart = v, Translator.CalendarSettings_WorkingDays_From))),
                vm, nameof(vm.IsWorkingHoursEnabled), WorkingOn),
            Bind.Enabled(Card(Translator.CalendarSettings_WorkingDays_To, null, WinoIconGlyph.None, Row(
                    Bind.PopUp(vm, days, nameof(vm.WorkingDayEndIndex), s => s.WorkingDayEndIndex, (s, v) => s.WorkingDayEndIndex = v, 140),
                    Bind.TimePicker(vm, nameof(vm.WorkingHourEnd), s => s.WorkingHourEnd, (s, v) => s.WorkingHourEnd = v, Translator.CalendarSettings_WorkingDays_To))),
                vm, nameof(vm.IsWorkingHoursEnabled), WorkingOn));

        // Hour height: slider plus the Windows sample cell that grows with the value.
        var slider = new NSSlider { MinValue = 40, MaxValue = 120, AltIncrementValue = 5, Continuous = true, TranslatesAutoresizingMaskIntoConstraints = false };
        slider.WidthAnchor.ConstraintEqualTo(260).Active = true;
        WinoAccessibility.Label(slider, Translator.CalendarSettings_HourHeight_Header);
        // One hour of the timeline: the hour labels sit on the hour lines, a fainter line marks the half hour.
        var sample = new WinoSurfaceView { Fill = WinoSettingsStyle.SubtleFill, Stroke = NSColor.Separator, CornerRadius = 4 };
        var sampleHeight = sample.HeightAnchor.ConstraintEqualTo(60);
        sampleHeight.Active = true;
        sample.WidthAnchor.ConstraintEqualTo(160).Active = true;
        var half = new WinoSeparator { Fill = NSColor.Separator };
        sample.AddSubview(half);
        var startLabel = Caption("00:00");
        var endLabel = Caption("01:00");
        var sampleRow = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        foreach (var view in new NSView[] { startLabel, endLabel, sample }) sampleRow.AddSubview(view);
        NSLayoutConstraint.ActivateConstraints(
        [
            half.LeadingAnchor.ConstraintEqualTo(sample.LeadingAnchor),
            half.TrailingAnchor.ConstraintEqualTo(sample.TrailingAnchor),
            half.CenterYAnchor.ConstraintEqualTo(sample.CenterYAnchor),
            startLabel.LeadingAnchor.ConstraintEqualTo(sampleRow.LeadingAnchor),
            endLabel.LeadingAnchor.ConstraintEqualTo(sampleRow.LeadingAnchor),
            sample.LeadingAnchor.ConstraintEqualTo(startLabel.TrailingAnchor, 8),
            sample.LeadingAnchor.ConstraintGreaterThanOrEqualTo(endLabel.TrailingAnchor, 8),
            sample.TrailingAnchor.ConstraintLessThanOrEqualTo(sampleRow.TrailingAnchor),
            startLabel.CenterYAnchor.ConstraintEqualTo(sample.TopAnchor),
            endLabel.CenterYAnchor.ConstraintEqualTo(sample.BottomAnchor),
            startLabel.TopAnchor.ConstraintEqualTo(sampleRow.TopAnchor),
            endLabel.BottomAnchor.ConstraintEqualTo(sampleRow.BottomAnchor)
        ]);
        Bind.Bind(vm, nameof(vm.CellHourHeight), s => s.CellHourHeight, value =>
        {
            slider.DoubleValue = value;
            sampleHeight.Constant = (nfloat)Math.Clamp(value, 40, 120);
        });
        Bind.OnActivated(slider, () => vm.CellHourHeight = Math.Round(slider.DoubleValue / 5) * 5);
        var hourHeight = Card(Translator.CalendarSettings_HourHeight_Header, Translator.CalendarSettings_HourHeight_Description);
        hourHeight.BottomContent = WinoLayout.VStack(12, slider, sampleRow);

        var timeFormat = Card(Translator.SettingsTimeFormat_Title, Translator.SettingsTimeFormat_Description, WinoIconGlyph.None,
            Bind.PopUp(vm, vm.TimeFormatPreferenceOptions, nameof(vm.SelectedTimeFormatPreferenceIndex), s => s.SelectedTimeFormatPreferenceIndex, (s, v) => s.SelectedTimeFormatPreferenceIndex = v));
        timeFormat.BottomContent = Bind.Label(vm, nameof(vm.TimedHourLabelPreview), s => s.TimedHourLabelPreview, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0);

        var format = Bind.TextField(vm, nameof(vm.TimedDayHeaderDateFormat), s => s.TimedDayHeaderDateFormat, (s, v) => s.TimedDayHeaderDateFormat = v,
            Translator.CalendarSettings_TimedDayHeaderFormat_Placeholder, 180);
        var presets = Bind.PopUp(vm, vm.TimedDayHeaderFormatPresets.ToList(), nameof(vm.SelectedTimedDayHeaderFormatPresetIndex),
            s => s.SelectedTimedDayHeaderFormatPresetIndex, (s, v) => s.SelectedTimedDayHeaderFormatPresetIndex = v, 140);
        // The format field and presets are too wide for the trailing column: they sit under the text with the preview.
        var headerFormat = Card(Translator.CalendarSettings_TimedDayHeaderFormat_Header, Translator.CalendarSettings_TimedDayHeaderFormat_Description);
        var headerFormatControls = WinoLayout.VStack(8, Row(format, presets),
            Bind.Label(vm, nameof(vm.TimedDayHeaderFormatPreview), s => s.TimedDayHeaderFormatPreview, WinoSettingsStyle.CardDescription, WinoStyle.SecondaryText, 0));
        headerFormat.BottomContent = headerFormatControls;

        var rendering = new WinoSettingsExpander(Translator.CalendarSettings_CalendarRendering_Header, Translator.CalendarSettings_CalendarRendering_Description, WinoIconGlyph.CalendarSettings, isExpanded: true);
        rendering.Add(hourHeight);
        rendering.Add(timeFormat);
        rendering.Add(headerFormat);

        AddGroup(null, firstDay, working, rendering);
    }
}
