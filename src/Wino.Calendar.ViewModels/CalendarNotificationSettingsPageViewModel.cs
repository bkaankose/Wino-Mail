using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Calendar.ViewModels;

public partial class CalendarNotificationSettingsPageViewModel : CalendarSettingsSectionViewModelBase
{
    public ObservableCollection<string> AvailableNotificationSounds { get; } = [];

    [ObservableProperty]
    public partial int SelectedDefaultReminderIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedDefaultSnoozeIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedNotificationSoundIndex { get; set; }

    public NotificationSoundEvent SelectedNotificationSoundEvent
        => GetNotificationSound(SelectedNotificationSoundIndex, NotificationSoundEvent.Reminder);

    public CalendarNotificationSettingsPageViewModel(
        IPreferencesService preferencesService,
        ICalendarService calendarService,
        IAccountService accountService)
        : base(preferencesService, calendarService, accountService)
    {
        LoadReminderOptions();
        LoadSnoozeOptions();
        LoadNotificationSoundOptions();

        SelectedDefaultReminderIndex = GetSelectedReminderIndex();
        SelectedDefaultSnoozeIndex = GetSelectedSnoozeIndex();
        SelectedNotificationSoundIndex = GetNotificationSoundIndex(
            PreferencesService.CalendarNotificationSoundEvent,
            NotificationSoundEvent.Reminder);

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

    partial void OnSelectedNotificationSoundIndexChanged(int value)
    {
        if (!IsLoaded)
            return;

        PreferencesService.CalendarNotificationSoundEvent = GetNotificationSound(value, NotificationSoundEvent.Reminder);
    }

    private void LoadNotificationSoundOptions()
    {
        foreach (var soundEvent in Enum.GetValues<NotificationSoundEvent>())
        {
            AvailableNotificationSounds.Add(GetNotificationSoundDisplayText(soundEvent));
        }
    }

    private static int GetNotificationSoundIndex(NotificationSoundEvent soundEvent, NotificationSoundEvent fallback)
    {
        var soundEvents = Enum.GetValues<NotificationSoundEvent>();
        var index = Array.IndexOf(soundEvents, soundEvent);
        return index >= 0 ? index : Array.IndexOf(soundEvents, fallback);
    }

    private static NotificationSoundEvent GetNotificationSound(int index, NotificationSoundEvent fallback)
    {
        var soundEvents = Enum.GetValues<NotificationSoundEvent>();
        return index >= 0 && index < soundEvents.Length ? soundEvents[index] : fallback;
    }

    private static string GetNotificationSoundDisplayText(NotificationSoundEvent soundEvent)
        => soundEvent switch
        {
            NotificationSoundEvent.Default => Translator.NotificationSound_Default,
            NotificationSoundEvent.IM => Translator.NotificationSound_IM,
            NotificationSoundEvent.Mail => Translator.NotificationSound_Mail,
            NotificationSoundEvent.Reminder => Translator.NotificationSound_Reminder,
            NotificationSoundEvent.SMS => Translator.NotificationSound_SMS,
            _ => soundEvent.ToString()
        };
}
