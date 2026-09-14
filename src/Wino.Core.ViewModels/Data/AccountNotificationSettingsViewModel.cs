using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.ViewModels.Data;

/// <summary>
/// One account row on the notifications settings page. Edits are pushed back through the change
/// callback so the page owns persistence and this stays presentation only.
/// </summary>
public sealed partial class AccountNotificationSettingsViewModel : ObservableObject
{
    private readonly Func<AccountNotificationSettingsViewModel, Task> _onChanged;
    private readonly bool _isInitialized;

    public AccountNotificationSettingsViewModel(
        MailAccount account,
        IReadOnlyList<MailNotificationScopeOption> scopeOptions,
        IReadOnlyList<MailNotificationContentOption> contentOptions,
        IReadOnlyList<NotificationSoundOption> soundOptions,
        IReadOnlyList<AccountQuietHoursStanceOption> quietHoursOptions,
        Func<AccountNotificationSettingsViewModel, Task> onChanged)
    {
        Account = account;
        _onChanged = onChanged;

        ScopeOptions = scopeOptions;
        ContentOptions = contentOptions;
        SoundOptions = soundOptions;
        QuietHoursOptions = quietHoursOptions;

        var preferences = account.Preferences;

        AreNotificationsEnabled = preferences.IsNotificationsEnabled;
        HasCustomNotificationSettings = preferences.HasCustomNotificationSettings;
        IsNewMailNotificationEnabled = preferences.IsNewMailNotificationEnabled;
        AreCalendarRemindersEnabled = preferences.AreCalendarRemindersEnabled;

        SelectedScope = NotificationOptionLookup.Find(scopeOptions, preferences.NotificationScope, option => option.Value);
        SelectedContent = NotificationOptionLookup.Find(contentOptions, preferences.NotificationContent, option => option.Value);
        SelectedSound = NotificationOptionLookup.Find(soundOptions, preferences.AccountNotificationSoundEvent, option => option.Value);
        SelectedQuietHoursStance = NotificationOptionLookup.Find(quietHoursOptions, preferences.QuietHoursStance, option => option.Value);

        _isInitialized = true;
    }

    public MailAccount Account { get; }

    public Guid AccountId => Account.Id;

    public string AccountAddress => Account.Address ?? string.Empty;

    public IReadOnlyList<MailNotificationScopeOption> ScopeOptions { get; }
    public IReadOnlyList<MailNotificationContentOption> ContentOptions { get; }
    public IReadOnlyList<NotificationSoundOption> SoundOptions { get; }
    public IReadOnlyList<AccountQuietHoursStanceOption> QuietHoursOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(AreOverrideFieldsEnabled))]
    public partial bool AreNotificationsEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(AreOverrideFieldsEnabled))]
    [NotifyPropertyChangedFor(nameof(IsFollowingDefaults))]
    public partial bool HasCustomNotificationSettings { get; set; }

    [ObservableProperty]
    public partial bool IsNewMailNotificationEnabled { get; set; }

    [ObservableProperty]
    public partial bool AreCalendarRemindersEnabled { get; set; }

    [ObservableProperty]
    public partial MailNotificationScopeOption SelectedScope { get; set; }

    [ObservableProperty]
    public partial MailNotificationContentOption SelectedContent { get; set; }

    [ObservableProperty]
    public partial NotificationSoundOption SelectedSound { get; set; }

    [ObservableProperty]
    public partial AccountQuietHoursStanceOption SelectedQuietHoursStance { get; set; }

    /// <summary>
    /// The inverse of <see cref="HasCustomNotificationSettings"/>, so the two radio buttons can bind
    /// two-way without a converter.
    /// </summary>
    public bool IsFollowingDefaults
    {
        get => !HasCustomNotificationSettings;
        set
        {
            if (value == IsFollowingDefaults)
                return;

            HasCustomNotificationSettings = !value;
        }
    }

    public bool AreOverrideFieldsEnabled => AreNotificationsEnabled && HasCustomNotificationSettings;

    public string StatusText
    {
        get
        {
            if (!AreNotificationsEnabled)
                return Translator.NotificationSettings_Account_Off;

            return HasCustomNotificationSettings
                ? Translator.NotificationSettings_Account_Customised
                : Translator.NotificationSettings_Account_FollowingDefaults;
        }
    }

    public NotificationSoundEvent SelectedSoundEvent => SelectedSound?.Value ?? NotificationSoundEvent.Mail;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // Derived properties carry no state of their own, so saving on them would be a redundant write.
        if (!_isInitialized || e.PropertyName is nameof(StatusText) or nameof(AreOverrideFieldsEnabled) or nameof(IsFollowingDefaults))
            return;

        _ = _onChanged(this);
    }
}
