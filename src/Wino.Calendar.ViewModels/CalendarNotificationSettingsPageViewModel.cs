using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Interfaces;

namespace Wino.Calendar.ViewModels;

public partial class CalendarNotificationSettingsPageViewModel : CalendarSettingsSectionViewModelBase
{
    [ObservableProperty]
    public partial int SelectedDefaultReminderIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedDefaultSnoozeIndex { get; set; }

    public CalendarNotificationSettingsPageViewModel(
        IPreferencesService preferencesService,
        ICalendarService calendarService,
        IAccountService accountService)
        : base(preferencesService, calendarService, accountService)
    {
        LoadReminderOptions();
        LoadSnoozeOptions();

        SelectedDefaultReminderIndex = GetSelectedReminderIndex();
        SelectedDefaultSnoozeIndex = GetSelectedSnoozeIndex();

        IsLoaded = true;
    }

    partial void OnSelectedDefaultReminderIndexChanged(int value)
    {
        if (!IsLoaded)
            return;

        SaveReminderIndex(value);
    }

    partial void OnSelectedDefaultSnoozeIndexChanged(int value)
    {
        if (!IsLoaded)
            return;

        SaveSnoozeIndex(value);
    }
}
