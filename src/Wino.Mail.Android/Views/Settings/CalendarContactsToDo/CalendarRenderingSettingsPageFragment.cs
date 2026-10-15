using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Widget;
using Wino.Calendar.ViewModels;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Android.Settings;
using Wino.Presentation.Android;
using View = Android.Views.View;

namespace Wino.Mail.Android.Views.Settings.CalendarContactsToDo;

/// <summary>
/// Calendar rendering: first day of the week, working days and hours, hour height, time format and the day header
/// date format with live previews (Windows CalendarRenderingSettingsPage, iOS
/// CalendarRenderingSettingsPageViewController). On phone width every pop-up, picker and field sits under its card text.
/// </summary>
public sealed class CalendarRenderingSettingsPageFragment : SettingsPageFragment<CalendarRenderingSettingsPageViewModel>
{
    protected override void BuildPage()
    {
        var vm = VM;
        var context = Ctx;
        var days = vm.DayNames.ToList();

        var firstDay = StackedCard(Translator.CalendarSettings_FirstDayOfWeek_Header, Translator.CalendarSettings_FirstDayOfWeek_Description, WinoIconGlyph.Calendar,
            Row(Bind.PopUp(vm, days, nameof(vm.SelectedFirstDayOfWeekIndex), s => s.SelectedFirstDayOfWeekIndex, (s, v) => s.SelectedFirstDayOfWeekIndex = v)));

        static bool WorkingOn(CalendarRenderingSettingsPageViewModel s) => s.IsWorkingHoursEnabled;
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

        // Hour height: slider in steps of 5 plus the Windows sample cell that grows with the value.
        var slider = Bind.Slider(vm, nameof(vm.CellHourHeight), s => s.CellHourHeight, (s, v) => s.CellHourHeight = Math.Round(v / 5) * 5, 40, 120, 5,
            Translator.CalendarSettings_HourHeight_Header);
        var sample = new FrameLayout(Ctx);
        var outline = new GradientDrawable();
        outline.SetColor(global::Android.Graphics.Color.Transparent);
        outline.SetStroke(WinoTokens.Px(Ctx, Resource.Dimension.wino_space_hairline), WinoColors.SecondaryText(Ctx));
        outline.SetCornerRadius(WinoTokens.PxF(Ctx, Resource.Dimension.wino_calendar_hour_sample_radius));
        sample.Background = outline;
        var half = new View(Ctx);
        half.SetBackgroundColor(WinoColors.SecondaryText(Ctx));
        sample.AddView(half, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, WinoTokens.Px(Ctx, Resource.Dimension.wino_space_hairline), GravityFlags.CenterVertical));
        var sampleLayout = new LinearLayout.LayoutParams(WinoTokens.Px(Ctx, Resource.Dimension.wino_calendar_hour_sample_width), ViewGroup.LayoutParams.WrapContent)
        {
            MarginStart = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s)
        };
        var sampleRow = new LinearLayout(Ctx) { Orientation = Orientation.Horizontal };
        sampleRow.SetGravity(GravityFlags.Bottom);
        sampleRow.AddView(Caption("00:00"));
        sampleRow.AddView(sample, sampleLayout);
        // The sample is as tall as the chosen hour height (dp, data from the ViewModel).
        Bind.Bind(vm, nameof(vm.CellHourHeight), s => s.CellHourHeight, value =>
        {
            sampleLayout.Height = (int)Math.Round(Math.Clamp(value, 40, 120) * WinoUnits.Density(context));
            sample.LayoutParameters = sampleLayout;
        });
        var hourHeight = Card(Translator.CalendarSettings_HourHeight_Header, Translator.CalendarSettings_HourHeight_Description);
        hourHeight.BottomContent = Stack(Resource.Dimension.wino_space_m, slider, sampleRow);

        var timeFormat = StackedCard(Translator.SettingsTimeFormat_Title, Translator.SettingsTimeFormat_Description, WinoIconGlyph.None, Stack(Resource.Dimension.wino_space_s,
            Row(Bind.PopUp(vm, vm.TimeFormatPreferenceOptions, nameof(vm.SelectedTimeFormatPreferenceIndex), s => s.SelectedTimeFormatPreferenceIndex, (s, v) => s.SelectedTimeFormatPreferenceIndex = v)),
            Bind.Label(vm, nameof(vm.TimedHourLabelPreview), s => s.TimedHourLabelPreview, maximumLines: 0)));

        var format = Bind.TextField(vm, nameof(vm.TimedDayHeaderDateFormat), s => s.TimedDayHeaderDateFormat, (s, v) => s.TimedDayHeaderDateFormat = v,
            Translator.CalendarSettings_TimedDayHeaderFormat_Placeholder);
        format.ContentDescription = Translator.CalendarSettings_TimedDayHeaderFormat_Header;
        if (format.EditText is { } field) field.InputType = InputTypes.ClassText | InputTypes.TextFlagNoSuggestions;
        var presets = Bind.PopUp(vm, vm.TimedDayHeaderFormatPresets.ToList(), nameof(vm.SelectedTimedDayHeaderFormatPresetIndex),
            s => s.SelectedTimedDayHeaderFormatPresetIndex, (s, v) => s.SelectedTimedDayHeaderFormatPresetIndex = v);
        var headerFormat = StackedCard(Translator.CalendarSettings_TimedDayHeaderFormat_Header, Translator.CalendarSettings_TimedDayHeaderFormat_Description, WinoIconGlyph.None,
            Stack(Resource.Dimension.wino_space_s, format, Row(presets),
                Bind.Label(vm, nameof(vm.TimedDayHeaderFormatPreview), s => s.TimedDayHeaderFormatPreview, maximumLines: 0)));

        var rendering = new WinoSettingsExpander(Ctx, Translator.CalendarSettings_CalendarRendering_Header, Translator.CalendarSettings_CalendarRendering_Description,
            WinoIconGlyph.CalendarSettings, isExpanded: true);
        rendering.Add(hourHeight);
        rendering.Add(timeFormat);
        rendering.Add(headerFormat);

        AddGroup(null, firstDay, working, rendering);
    }

    /// <summary>The day pop-up filling the line with its time picker trailing.</summary>
    private LinearLayout DayAndTime(View day, View time)
    {
        var row = new LinearLayout(Ctx) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.AddView(day, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        row.AddView(time, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent) { MarginStart = WinoTokens.Px(Ctx, Resource.Dimension.wino_space_s) });
        return row;
    }
}
