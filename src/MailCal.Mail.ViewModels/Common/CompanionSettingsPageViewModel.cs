using System.Collections.Generic;
using System.Linq;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Navigation;
using CommunityToolkit.Mvvm.Messaging;
using MailCal.Messaging.Client.Shell;

namespace MailCal.Core.ViewModels;

/// <summary>One choice of the companion's unread message behavior, with its display text.</summary>
public sealed record CompanionUnreadBehaviorOption(CompanionUnreadMessageBehavior Behavior, string DisplayText);

/// <summary>
/// Settings › Companion. The enable switch, the unread message behavior and the four content
/// toggles read and write the preferences directly. The global shortcut stays in the Windows page
/// because it registers a system hotkey there.
/// </summary>
public partial class CompanionSettingsPageViewModel(IPreferencesService preferencesService) : CoreBaseViewModel,
    IRecipient<PreferenceChangedMessage>
{

    public IPreferencesService PreferencesService { get; } = preferencesService;

    public IReadOnlyList<CompanionUnreadBehaviorOption> UnreadBehaviorOptions { get; } = (CompanionUnreadBehaviorOption[])
    [
        new(CompanionUnreadMessageBehavior.AfterAppSession, Translator.CompanionSettings_UnreadBehavior_AfterAppSession),
        new(CompanionUnreadMessageBehavior.Everything, Translator.CompanionSettings_UnreadBehavior_Everything)
    ];

    public bool IsCompanionEnabled
    {
        get => PreferencesService.IsCompanionEnabled;
        set
        {
            if (PreferencesService.IsCompanionEnabled == value) return;
            PreferencesService.IsCompanionEnabled = value;
            RaiseCompanionProperties();
        }
    }

    public bool ShowCalendar
    {
        get => PreferencesService.ShowCalendarInCompanion;
        set
        {
            if (PreferencesService.ShowCalendarInCompanion == value) return;
            PreferencesService.ShowCalendarInCompanion = value;
            OnPropertyChanged();
        }
    }

    public bool ShowUnreadMail
    {
        get => PreferencesService.ShowUnreadMailInCompanion;
        set
        {
            if (PreferencesService.ShowUnreadMailInCompanion == value) return;
            PreferencesService.ShowUnreadMailInCompanion = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsUnreadBehaviorEnabled));
        }
    }

    public bool ShowTasks
    {
        get => PreferencesService.ShowTasksInCompanion;
        set
        {
            if (PreferencesService.ShowTasksInCompanion == value) return;
            PreferencesService.ShowTasksInCompanion = value;
            OnPropertyChanged();
        }
    }

    public bool ShowFavoriteContacts
    {
        get => PreferencesService.ShowFavoriteContactsInCompanion;
        set
        {
            if (PreferencesService.ShowFavoriteContactsInCompanion == value) return;
            PreferencesService.ShowFavoriteContactsInCompanion = value;
            OnPropertyChanged();
        }
    }

    public CompanionUnreadBehaviorOption SelectedUnreadBehavior
    {
        get => UnreadBehaviorOptions.FirstOrDefault(option => option.Behavior == PreferencesService.CompanionUnreadMessageBehavior)
            ?? UnreadBehaviorOptions[0];
        set
        {
            if (value is null || PreferencesService.CompanionUnreadMessageBehavior == value.Behavior) return;
            PreferencesService.CompanionUnreadMessageBehavior = value.Behavior;
            OnPropertyChanged();
        }
    }

    /// <summary>The content section follows the enable switch.</summary>
    public bool IsContentEnabled => IsCompanionEnabled;

    /// <summary>The unread behavior only matters while unread messages are shown.</summary>
    public bool IsUnreadBehaviorEnabled => IsCompanionEnabled && ShowUnreadMail;

    protected override void RegisterRecipients()
    {
        base.RegisterRecipients();

        Messenger.Register<PreferenceChangedMessage>(this);
    }

    protected override void UnregisterRecipients()
    {
        base.UnregisterRecipients();

        Messenger.Unregister<PreferenceChangedMessage>(this);
    }

    /// <summary>Follows changes made elsewhere (another window, the Windows page's own bindings).</summary>
    public void Receive(PreferenceChangedMessage message)
    {
        switch (message.PropertyName)
        {
            case nameof(IPreferencesService.IsCompanionEnabled):
                _ = ExecuteUIThread(RaiseCompanionProperties);
                break;
            case nameof(IPreferencesService.ShowCalendarInCompanion):
                _ = ExecuteUIThread(() => OnPropertyChanged(nameof(ShowCalendar)));
                break;
            case nameof(IPreferencesService.ShowUnreadMailInCompanion):
                _ = ExecuteUIThread(() =>
                {
                    OnPropertyChanged(nameof(ShowUnreadMail));
                    OnPropertyChanged(nameof(IsUnreadBehaviorEnabled));
                });
                break;
            case nameof(IPreferencesService.ShowTasksInCompanion):
                _ = ExecuteUIThread(() => OnPropertyChanged(nameof(ShowTasks)));
                break;
            case nameof(IPreferencesService.ShowFavoriteContactsInCompanion):
                _ = ExecuteUIThread(() => OnPropertyChanged(nameof(ShowFavoriteContacts)));
                break;
            case nameof(IPreferencesService.CompanionUnreadMessageBehavior):
                _ = ExecuteUIThread(() => OnPropertyChanged(nameof(SelectedUnreadBehavior)));
                break;
        }
    }

    private void RaiseCompanionProperties()
    {
        OnPropertyChanged(nameof(IsCompanionEnabled));
        OnPropertyChanged(nameof(IsContentEnabled));
        OnPropertyChanged(nameof(IsUnreadBehaviorEnabled));
    }
}
