using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Translations;
using Wino.Core.ViewModels;
using Wino.Messaging.Client.Calendar;

namespace Wino.Calendar.ViewModels;

public partial class CalendarSettingsPageViewModel : CalendarBaseViewModel
{
    [ObservableProperty]
    private double _cellHourHeight;

    [ObservableProperty]
    private int _selectedFirstDayOfWeekIndex;

    [ObservableProperty]
    private bool _is24HourHeaders;

    [ObservableProperty]
    private TimeSpan _workingHourStart;

    [ObservableProperty]
    private TimeSpan _workingHourEnd;

    [ObservableProperty]
    private List<string> _dayNames = [];

    [ObservableProperty]
    private int _workingDayStartIndex;

    [ObservableProperty]
    private int _workingDayEndIndex;

    public IPreferencesService PreferencesService { get; }

    private readonly bool _isLoaded = false;

    public CalendarSettingsPageViewModel(IPreferencesService preferencesService)
    {
        PreferencesService = preferencesService;

        var currentLanguageLanguageCode = WinoTranslationDictionary.GetLanguageFileNameRelativePath(preferencesService.CurrentLanguage);

        var cultureInfo = new CultureInfo(currentLanguageLanguageCode);

        // Populate the day names list
        for (var i = 0; i < 7; i++)
        {
            _dayNames.Add(cultureInfo.DateTimeFormat.DayNames[i]);
        }

        var cultureFirstDayName = cultureInfo.DateTimeFormat.GetDayName(preferencesService.FirstDayOfWeek);

        _selectedFirstDayOfWeekIndex = _dayNames.IndexOf(cultureFirstDayName);
        _is24HourHeaders = preferencesService.Prefer24HourTimeFormat;
        _workingHourStart = preferencesService.WorkingHourStart;
        _workingHourEnd = preferencesService.WorkingHourEnd;
        _cellHourHeight = preferencesService.HourHeight;

        _workingDayStartIndex = _dayNames.IndexOf(cultureInfo.DateTimeFormat.GetDayName(preferencesService.WorkingDayStart));
        _workingDayEndIndex = _dayNames.IndexOf(cultureInfo.DateTimeFormat.GetDayName(preferencesService.WorkingDayEnd));

        _isLoaded = true;
    }

    partial void OnCellHourHeightChanged(double oldValue, double newValue) => SaveSettings();
    partial void OnIs24HourHeadersChanged(bool value) => SaveSettings();
    partial void OnSelectedFirstDayOfWeekIndexChanged(int value) => SaveSettings();
    partial void OnWorkingHourStartChanged(TimeSpan value) => SaveSettings();
    partial void OnWorkingHourEndChanged(TimeSpan value) => SaveSettings();
    partial void OnWorkingDayStartIndexChanged(int value) => SaveSettings();
    partial void OnWorkingDayEndIndexChanged(int value) => SaveSettings();

    public void SaveSettings()
    {
        if (!_isLoaded) return;

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
        PreferencesService.WorkingHourStart = WorkingHourStart;
        PreferencesService.WorkingHourEnd = WorkingHourEnd;
        PreferencesService.HourHeight = CellHourHeight;

        Messenger.Send(new CalendarSettingsUpdatedMessage());
    }
}
