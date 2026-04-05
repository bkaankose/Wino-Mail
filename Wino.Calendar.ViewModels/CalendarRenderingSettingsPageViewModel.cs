using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Interfaces;

namespace Wino.Calendar.ViewModels;

public partial class CalendarRenderingSettingsPageViewModel : CalendarSettingsSectionViewModelBase
{
    [ObservableProperty]
    public partial double CellHourHeight { get; set; }

    [ObservableProperty]
    public partial int SelectedFirstDayOfWeekIndex { get; set; }

    [ObservableProperty]
    public partial bool Is24HourHeaders { get; set; }

    [ObservableProperty]
    public partial bool IsWorkingHoursEnabled { get; set; }

    [ObservableProperty]
    public partial TimeSpan WorkingHourStart { get; set; }

    [ObservableProperty]
    public partial TimeSpan WorkingHourEnd { get; set; }

    [ObservableProperty]
    public partial int WorkingDayStartIndex { get; set; }

    [ObservableProperty]
    public partial int WorkingDayEndIndex { get; set; }

    [ObservableProperty]
    public partial string TimedDayHeaderDateFormat { get; set; } = "ddd dd";

    [ObservableProperty]
    public partial int SelectedTimedDayHeaderFormatPresetIndex { get; set; } = -1;

    public CalendarRenderingSettingsPageViewModel(
        IPreferencesService preferencesService,
        ICalendarService calendarService,
        IAccountService accountService)
        : base(preferencesService, calendarService, accountService)
    {
        SelectedFirstDayOfWeekIndex = DayNames.IndexOf(CalendarCulture.DateTimeFormat.GetDayName(preferencesService.FirstDayOfWeek));
        Is24HourHeaders = preferencesService.Prefer24HourTimeFormat;
        IsWorkingHoursEnabled = preferencesService.IsWorkingHoursEnabled;
        WorkingHourStart = preferencesService.WorkingHourStart;
        WorkingHourEnd = preferencesService.WorkingHourEnd;
        CellHourHeight = preferencesService.HourHeight;
        WorkingDayStartIndex = DayNames.IndexOf(CalendarCulture.DateTimeFormat.GetDayName(preferencesService.WorkingDayStart));
        WorkingDayEndIndex = DayNames.IndexOf(CalendarCulture.DateTimeFormat.GetDayName(preferencesService.WorkingDayEnd));
        TimedDayHeaderDateFormat = preferencesService.CalendarTimedDayHeaderDateFormat;
        SelectedTimedDayHeaderFormatPresetIndex = TimedDayHeaderFormatPresets.IndexOf(TimedDayHeaderDateFormat);

        IsLoaded = true;
    }

    partial void OnCellHourHeightChanged(double oldValue, double newValue) => SaveSettings();

    partial void OnIs24HourHeadersChanged(bool value)
    {
        OnPropertyChanged(nameof(TimedHourLabelPreview));
        SaveSettings();
    }

    partial void OnSelectedFirstDayOfWeekIndexChanged(int value) => SaveSettings();
    partial void OnIsWorkingHoursEnabledChanged(bool value) => SaveSettings();
    partial void OnWorkingHourStartChanged(TimeSpan value) => SaveSettings();
    partial void OnWorkingHourEndChanged(TimeSpan value) => SaveSettings();
    partial void OnWorkingDayStartIndexChanged(int value) => SaveSettings();
    partial void OnWorkingDayEndIndexChanged(int value) => SaveSettings();

    partial void OnTimedDayHeaderDateFormatChanged(string value)
    {
        OnPropertyChanged(nameof(TimedDayHeaderFormatPreview));
        OnPropertyChanged(nameof(TimedHourLabelPreview));

        var normalizedFormat = string.IsNullOrWhiteSpace(value) ? "ddd dd" : value.Trim();
        var matchingPresetIndex = TimedDayHeaderFormatPresets
            .Select((format, index) => new { format, index })
            .Where(item => string.Equals(item.format, normalizedFormat, StringComparison.Ordinal))
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();

        if (SelectedTimedDayHeaderFormatPresetIndex != matchingPresetIndex)
        {
            SelectedTimedDayHeaderFormatPresetIndex = matchingPresetIndex;
        }

        SaveSettings();
    }

    partial void OnSelectedTimedDayHeaderFormatPresetIndexChanged(int value)
    {
        if (value < 0 || value >= TimedDayHeaderFormatPresets.Count)
            return;

        var selectedPreset = TimedDayHeaderFormatPresets[value];
        if (string.Equals(TimedDayHeaderDateFormat, selectedPreset, StringComparison.Ordinal))
            return;

        TimedDayHeaderDateFormat = selectedPreset;
    }

    public string TimedDayHeaderFormatPreview
    {
        get
        {
            var format = string.IsNullOrWhiteSpace(TimedDayHeaderDateFormat) ? "ddd dd" : TimedDayHeaderDateFormat.Trim();
            var previewDates = new[]
            {
                new DateTime(2026, 3, 23),
                new DateTime(2026, 3, 24),
                new DateTime(2026, 3, 25)
            };

            try
            {
                return string.Join(" · ", previewDates.Select(date => date.ToString(format, CalendarCulture)));
            }
            catch (FormatException)
            {
                return string.Join(" · ", previewDates.Select(date => date.ToString("ddd dd", CalendarCulture)));
            }
        }
    }

    public string TimedHourLabelPreview
        => string.Join("  ·  ", new[] { 0, 9, 14, 24 }.Select(CurrentSettingsPreviewLabel));

    private string CurrentSettingsPreviewLabel(int hour)
    {
        if (Is24HourHeaders)
            return hour.ToString(CalendarCulture);

        var displayHour = hour % 24;
        return DateTime.Today.AddHours(displayHour).ToString("h tt", CalendarCulture);
    }

    private void SaveSettings()
    {
        if (!IsLoaded)
            return;

        PreferencesService.FirstDayOfWeek = SelectedFirstDayOfWeekIndex switch
        {
            0 => DayOfWeek.Sunday,
            1 => DayOfWeek.Monday,
            2 => DayOfWeek.Tuesday,
            3 => DayOfWeek.Wednesday,
            4 => DayOfWeek.Thursday,
            5 => DayOfWeek.Friday,
            6 => DayOfWeek.Saturday,
            _ => throw new ArgumentOutOfRangeException()
        };

        PreferencesService.WorkingDayStart = WorkingDayStartIndex switch
        {
            0 => DayOfWeek.Sunday,
            1 => DayOfWeek.Monday,
            2 => DayOfWeek.Tuesday,
            3 => DayOfWeek.Wednesday,
            4 => DayOfWeek.Thursday,
            5 => DayOfWeek.Friday,
            6 => DayOfWeek.Saturday,
            _ => throw new ArgumentOutOfRangeException()
        };

        PreferencesService.WorkingDayEnd = WorkingDayEndIndex switch
        {
            0 => DayOfWeek.Sunday,
            1 => DayOfWeek.Monday,
            2 => DayOfWeek.Tuesday,
            3 => DayOfWeek.Wednesday,
            4 => DayOfWeek.Thursday,
            5 => DayOfWeek.Friday,
            6 => DayOfWeek.Saturday,
            _ => throw new ArgumentOutOfRangeException()
        };

        PreferencesService.Prefer24HourTimeFormat = Is24HourHeaders;
        PreferencesService.IsWorkingHoursEnabled = IsWorkingHoursEnabled;
        PreferencesService.WorkingHourStart = WorkingHourStart;
        PreferencesService.WorkingHourEnd = WorkingHourEnd;
        PreferencesService.HourHeight = CellHourHeight;
        PreferencesService.CalendarTimedDayHeaderDateFormat = TimedDayHeaderDateFormat;
    }
}
