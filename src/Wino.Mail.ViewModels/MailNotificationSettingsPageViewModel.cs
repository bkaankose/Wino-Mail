using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain;

namespace Wino.Mail.ViewModels;

public partial class MailNotificationSettingsPageViewModel : MailBaseViewModel
{
    private static readonly MailOperation[] SupportedMailNotificationActions =
    [
        MailOperation.MarkAsRead,
        MailOperation.SoftDelete,
        MailOperation.MoveToJunk,
        MailOperation.Archive,
        MailOperation.Reply,
        MailOperation.ReplyAll,
        MailOperation.Forward
    ];

    private readonly IPreferencesService _preferencesService;
    private bool _isUpdatingSelection;
    private bool _isLoaded;

    public ObservableCollection<MailNotificationActionOption> AvailableNotificationActions { get; } = [];
    public ObservableCollection<string> AvailableNotificationSounds { get; } = [];

    [ObservableProperty]
    public partial MailNotificationActionOption SelectedFirstAction { get; set; }

    [ObservableProperty]
    public partial MailNotificationActionOption SelectedSecondAction { get; set; }

    [ObservableProperty]
    public partial int SelectedNotificationSoundIndex { get; set; }

    public NotificationSoundEvent SelectedNotificationSoundEvent
        => GetNotificationSound(SelectedNotificationSoundIndex, NotificationSoundEvent.Mail);

    public MailNotificationSettingsPageViewModel(IPreferencesService preferencesService)
    {
        _preferencesService = preferencesService;

        foreach (var action in SupportedMailNotificationActions)
        {
            AvailableNotificationActions.Add(new MailNotificationActionOption(action, GetOperationDisplayText(action)));
        }

        foreach (var soundEvent in Enum.GetValues<NotificationSoundEvent>())
        {
            AvailableNotificationSounds.Add(GetNotificationSoundDisplayText(soundEvent));
        }

        InitializeSelections();
        SelectedNotificationSoundIndex = GetNotificationSoundIndex(
            _preferencesService.MailNotificationSoundEvent,
            NotificationSoundEvent.Mail);
        _isLoaded = true;
    }

    partial void OnSelectedFirstActionChanged(MailNotificationActionOption value)
    {
        if (!_isLoaded || _isUpdatingSelection || value == null)
            return;

        EnsureDistinctSelections(changedSelection: value, isFirstSelection: true);
        _preferencesService.FirstMailNotificationAction = value.Operation;
    }

    partial void OnSelectedSecondActionChanged(MailNotificationActionOption value)
    {
        if (!_isLoaded || _isUpdatingSelection || value == null)
            return;

        EnsureDistinctSelections(changedSelection: value, isFirstSelection: false);
        _preferencesService.SecondMailNotificationAction = value.Operation;
    }

    partial void OnSelectedNotificationSoundIndexChanged(int value)
    {
        if (!_isLoaded)
            return;

        _preferencesService.MailNotificationSoundEvent = GetNotificationSound(value, NotificationSoundEvent.Mail);
    }

    private void InitializeSelections()
    {
        var firstAction = ResolveSupportedAction(_preferencesService.FirstMailNotificationAction, MailOperation.MarkAsRead);
        var secondAction = ResolveSupportedAction(_preferencesService.SecondMailNotificationAction, MailOperation.SoftDelete);

        if (secondAction == firstAction)
        {
            secondAction = GetFallbackDistinctAction(firstAction);
        }

        SelectedFirstAction = GetOption(firstAction);
        SelectedSecondAction = GetOption(secondAction);

        _preferencesService.FirstMailNotificationAction = firstAction;
        _preferencesService.SecondMailNotificationAction = secondAction;
    }

    private void EnsureDistinctSelections(MailNotificationActionOption changedSelection, bool isFirstSelection)
    {
        var otherSelection = isFirstSelection ? SelectedSecondAction : SelectedFirstAction;
        if (otherSelection?.Operation != changedSelection.Operation)
            return;

        _isUpdatingSelection = true;

        var fallbackAction = GetFallbackDistinctAction(changedSelection.Operation);
        var fallbackOption = GetOption(fallbackAction);

        if (isFirstSelection)
        {
            SelectedSecondAction = fallbackOption;
            _preferencesService.SecondMailNotificationAction = fallbackAction;
        }
        else
        {
            SelectedFirstAction = fallbackOption;
            _preferencesService.FirstMailNotificationAction = fallbackAction;
        }

        _isUpdatingSelection = false;
    }

    private MailNotificationActionOption GetOption(MailOperation action)
        => AvailableNotificationActions.First(option => option.Operation == action);

    private static MailOperation ResolveSupportedAction(MailOperation action, MailOperation fallbackAction)
        => SupportedMailNotificationActions.Contains(action) ? action : fallbackAction;

    private static MailOperation GetFallbackDistinctAction(MailOperation excludedAction)
        => SupportedMailNotificationActions.First(action => action != excludedAction);

    private static string GetOperationDisplayText(MailOperation action)
        => action switch
        {
            MailOperation.MarkAsRead => Translator.MailOperation_MarkAsRead,
            MailOperation.SoftDelete => Translator.MailOperation_Delete,
            MailOperation.MoveToJunk => Translator.MailOperation_MarkAsJunk,
            MailOperation.Archive => Translator.MailOperation_Archive,
            MailOperation.Reply => Translator.MailOperation_Reply,
            MailOperation.ReplyAll => Translator.MailOperation_ReplyAll,
            MailOperation.Forward => Translator.MailOperation_Forward,
            _ => action.ToString()
        };

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

public sealed class MailNotificationActionOption(MailOperation operation, string displayText)
{
    public MailOperation Operation { get; } = operation;
    public string DisplayText { get; } = displayText;
}
