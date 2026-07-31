using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Calendar.ViewModels;

public partial class CalendarPreferenceSettingsPageViewModel : CalendarSettingsSectionViewModelBase
{
    [ObservableProperty]
    public partial CalendarNewEventBehaviorOption SelectedNewEventBehaviorOption { get; set; }

    [ObservableProperty]
    public partial AccountCalendarViewModel SelectedNewEventCalendar { get; set; }

    public bool ShouldShowSpecificNewEventCalendar
        => SelectedNewEventBehaviorOption?.Behavior == NewEventButtonBehavior.AlwaysUseSpecificCalendar;

    public CalendarPreferenceSettingsPageViewModel(
        IPreferencesService preferencesService,
        ICalendarService calendarService,
        IAccountService accountService)
        : base(preferencesService, calendarService, accountService)
    {
        LoadNewEventBehaviorOptions();
        SelectedNewEventBehaviorOption = GetSelectedNewEventBehaviorOption();

        IsLoaded = true;
        LoadCalendarsAsync(ApplyStoredNewEventCalendarPreference);
    }

    partial void OnSelectedNewEventBehaviorOptionChanged(CalendarNewEventBehaviorOption value)
    {
        if (!IsLoaded)
            return;

        OnPropertyChanged(nameof(ShouldShowSpecificNewEventCalendar));
        SaveNewEventBehavior(SelectedNewEventBehaviorOption, SelectedNewEventCalendar);
    }

    partial void OnSelectedNewEventCalendarChanged(AccountCalendarViewModel value)
    {
        if (!IsLoaded)
            return;

        SaveNewEventBehavior(SelectedNewEventBehaviorOption, value);
    }

    private void ApplyStoredNewEventCalendarPreference()
    {
        var configuredCalendar = ResolveSelectedNewEventCalendar();
        if (PreferencesService.NewEventButtonBehavior == NewEventButtonBehavior.AlwaysUseSpecificCalendar && configuredCalendar == null)
        {
            SelectedNewEventBehaviorOption = NewEventBehaviorOptions.First(option => option.Behavior == NewEventButtonBehavior.AskEachTime);
            SelectedNewEventCalendar = null;
            return;
        }

        SelectedNewEventCalendar = configuredCalendar ?? ResolveFallbackNewEventCalendar();
    }
}
