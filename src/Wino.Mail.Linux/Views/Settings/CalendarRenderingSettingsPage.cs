using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.Settings;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels.Calendar;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// Calendar rendering (Windows CalendarRenderingSettingsPage): first day of week, working days and hours, hour height
/// with its sample, time format and the timed day header format.
/// </summary>
internal sealed class CalendarRenderingSettingsPage(CalendarRenderingSettingsPageViewModel viewModel, WinoViewServices services)
    : SettingsPageView<CalendarRenderingSettingsPageViewModel>(viewModel, services)
{
    private const int MinimumHourHeight = 40;
    private const int MaximumHourHeight = 120;
    private const int HourHeightStep = 5;

    protected override void BuildPage()
    {
        var vm = ViewModel;
        string[] days = [.. vm.DayNames];
        static bool WorkingOn(CalendarRenderingSettingsPageViewModel s) => s.IsWorkingHoursEnabled;

        var from = WinoSettingsRows.Action(Translator.CalendarSettings_WorkingDays_From);
        WinoSettingsRows.Suffix(from, Settings.ChoiceSuffix(vm, Translator.CalendarSettings_WorkingDays_From, days,
            nameof(vm.WorkingDayStartIndex), static s => s.WorkingDayStartIndex, static (s, v) => s.WorkingDayStartIndex = v));
        WinoSettingsRows.Suffix(from, Settings.TimeSuffix(vm, Translator.CalendarSettings_WorkingDays_From,
            nameof(vm.WorkingHourStart), static s => s.WorkingHourStart, static (s, v) => s.WorkingHourStart = v));
        Settings.Enabled(from, vm, nameof(vm.IsWorkingHoursEnabled), WorkingOn);
        var to = WinoSettingsRows.Action(Translator.CalendarSettings_WorkingDays_To);
        WinoSettingsRows.Suffix(to, Settings.ChoiceSuffix(vm, Translator.CalendarSettings_WorkingDays_To, days,
            nameof(vm.WorkingDayEndIndex), static s => s.WorkingDayEndIndex, static (s, v) => s.WorkingDayEndIndex = v));
        WinoSettingsRows.Suffix(to, Settings.TimeSuffix(vm, Translator.CalendarSettings_WorkingDays_To,
            nameof(vm.WorkingHourEnd), static s => s.WorkingHourEnd, static (s, v) => s.WorkingHourEnd = v));
        Settings.Enabled(to, vm, nameof(vm.IsWorkingHoursEnabled), WorkingOn);
        var working = WinoSettingsRows.Expander(Translator.CalendarSettings_WorkingDays_Header, Translator.CalendarSettings_WorkingDays_Description, WinoIconGlyph.CalendarWorkWeek, false,
            Settings.Switch(vm, Translator.CalendarSettings_HighlightWorkingHours_Label, null, WinoIconGlyph.None,
                nameof(vm.IsWorkingHoursEnabled), WorkingOn, static (s, v) => s.IsWorkingHoursEnabled = v),
            from, to);

        var timeFormat = Settings.Choice(vm, Translator.SettingsTimeFormat_Title, Translator.SettingsTimeFormat_Description, WinoIconGlyph.None,
            [.. vm.TimeFormatPreferenceOptions], nameof(vm.SelectedTimeFormatPreferenceIndex),
            static s => s.SelectedTimeFormatPreferenceIndex, static (s, v) => s.SelectedTimeFormatPreferenceIndex = v);

        var headerFormat = WinoSettingsRows.Action(Translator.CalendarSettings_TimedDayHeaderFormat_Header, Translator.CalendarSettings_TimedDayHeaderFormat_Description);
        var formatControls = Adw.WrapBox.New();
        formatControls.SetChildSpacing(8);
        formatControls.SetLineSpacing(8);
        formatControls.Append(Settings.TextSuffix(vm, Translator.CalendarSettings_TimedDayHeaderFormat_Header, Translator.CalendarSettings_TimedDayHeaderFormat_Placeholder,
            nameof(vm.TimedDayHeaderDateFormat), static s => s.TimedDayHeaderDateFormat, static (s, v) => s.TimedDayHeaderDateFormat = v, 14));
        formatControls.Append(Settings.ChoiceSuffix(vm, Translator.CalendarSettings_TimedDayHeaderFormat_Header, [.. vm.TimedDayHeaderFormatPresets],
            nameof(vm.SelectedTimedDayHeaderFormatPresetIndex), static s => s.SelectedTimedDayHeaderFormatPresetIndex, static (s, v) => s.SelectedTimedDayHeaderFormatPresetIndex = v));

        var rendering = WinoSettingsRows.Expander(Translator.CalendarSettings_CalendarRendering_Header, Translator.CalendarSettings_CalendarRendering_Description, WinoIconGlyph.CalendarSettings, true,
            WinoSettingsRows.Action(Translator.CalendarSettings_HourHeight_Header, Translator.CalendarSettings_HourHeight_Description),
            HourHeight(vm),
            timeFormat,
            Caption(vm, nameof(vm.TimedHourLabelPreview), static s => s.TimedHourLabelPreview),
            headerFormat,
            // The format entry and presets are too wide for the suffix column, so they get a row of their own.
            WinoSettingsRows.Custom(formatControls, Translator.CalendarSettings_TimedDayHeaderFormat_Header),
            Caption(vm, nameof(vm.TimedDayHeaderFormatPreview), static s => s.TimedDayHeaderFormatPreview));

        AddGroup(null,
            Settings.Choice(vm, Translator.CalendarSettings_FirstDayOfWeek_Header, Translator.CalendarSettings_FirstDayOfWeek_Description, WinoIconGlyph.Calendar,
                days, nameof(vm.SelectedFirstDayOfWeekIndex), static s => s.SelectedFirstDayOfWeekIndex, static (s, v) => s.SelectedFirstDayOfWeekIndex = v),
            working,
            rendering);
    }

    private Adw.PreferencesRow Caption(CalendarRenderingSettingsPageViewModel vm, string property, Func<CalendarRenderingSettingsPageViewModel, string?> read)
    {
        var caption = WinoSettingsRows.Caption(null);
        Settings.Bind(vm, property, read, text => caption.SetText(text ?? string.Empty));
        return WinoSettingsRows.Custom(caption);
    }

    /// <summary>The slider snaps to 5 and writes only a changed value; the sample cell grows with it.</summary>
    private Adw.PreferencesRow HourHeight(CalendarRenderingSettingsPageViewModel vm)
    {
        var scale = Gtk.Scale.NewWithRange(Gtk.Orientation.Horizontal, MinimumHourHeight, MaximumHourHeight, HourHeightStep);
        scale.SetRoundDigits(0);
        scale.SetDrawValue(true);
        scale.SetSizeRequest(260, -1);
        scale.SetHalign(Gtk.Align.Start);
        WinoA11y.Label(scale, Translator.CalendarSettings_HourHeight_Header);

        var sample = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        sample.AddCssClass("card");
        sample.SetSizeRequest(160, 60);
        sample.SetValign(Gtk.Align.Start);
        var half = Gtk.Separator.New(Gtk.Orientation.Horizontal);
        half.SetVexpand(true);
        half.SetValign(Gtk.Align.Center);
        sample.Append(half);
        var start = Gtk.Label.New("00:00");
        var end = Gtk.Label.New("01:00");
        foreach (var label in new[] { start, end })
        {
            label.AddCssClass("caption");
            label.AddCssClass("dim-label");
        }
        var hours = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        start.SetValign(Gtk.Align.Start);
        end.SetValign(Gtk.Align.End);
        end.SetVexpand(true);
        hours.Append(start);
        hours.Append(end);
        var sampleRow = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        sampleRow.Append(hours);
        sampleRow.Append(sample);

        Settings.Bind(vm, nameof(vm.CellHourHeight), static s => s.CellHourHeight, value =>
        {
            if (Math.Abs(scale.GetValue() - value) > 0.1) scale.SetValue(value);
            var height = (int)Math.Clamp(value, MinimumHourHeight, MaximumHourHeight);
            sample.SetSizeRequest(160, height);
            hours.SetSizeRequest(-1, height);
        });
        Settings.Own(GtkSignals.Connect<Gtk.Range>(h => scale.OnValueChanged += h, h => scale.OnValueChanged -= h, (_, _) =>
        {
            var snapped = Math.Round(scale.GetValue() / HourHeightStep) * HourHeightStep;
            if (Math.Abs(snapped - vm.CellHourHeight) > 0.1) vm.CellHourHeight = snapped;
        }));

        var box = Gtk.Box.New(Gtk.Orientation.Vertical, 12);
        box.Append(scale);
        box.Append(sampleRow);
        return WinoSettingsRows.Custom(box, Translator.CalendarSettings_HourHeight_Header);
    }
}
