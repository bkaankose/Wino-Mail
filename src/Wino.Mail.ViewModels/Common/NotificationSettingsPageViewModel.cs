using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.Domain.Models.PushRelay;
using Wino.Core.Domain.Translations;
using Wino.Core.ViewModels.Data;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Messaging.Client.Shell;

namespace Wino.Core.ViewModels;

/// <summary>
/// Backs Settings -> General -> Notifications, which owns every alert Wino raises: the snooze state,
/// the quiet hours schedule, the per-type defaults and the per-account overrides.
/// </summary>
public partial class NotificationSettingsPageViewModel : CoreBaseViewModel,
    IRecipient<PreferenceChangedMessage>
{
    /// <summary>
    /// Actions offered as mail notification buttons. Kept narrow on purpose: a toast fits two.
    /// </summary>
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

    /// <summary>
    /// Durations offered in the snooze dropdown, in the order they are shown. Custom is deliberately
    /// absent: it needs a time picker, so it is only reachable from this page.
    /// </summary>
    private static readonly NotificationSnoozePreset[] SnoozePresetOrder =
    [
        NotificationSnoozePreset.ThirtyMinutes,
        NotificationSnoozePreset.OneHour,
        NotificationSnoozePreset.TwoHours,
        NotificationSnoozePreset.RestOfDay,
        NotificationSnoozePreset.UntilTomorrowMorning,
        NotificationSnoozePreset.UntilTurnedBackOn
    ];

    private static readonly int[] TaskSnoozeMinuteOptions = [10, 30, 60, 120];

    private readonly IAccountService _accountService;
    private readonly ICalendarService _calendarService;
    private readonly INotificationPolicyService _notificationPolicyService;
    private readonly IUserPresenceStateProvider _userPresenceStateProvider;
    private readonly IPushRelayRegistrationService _pushRelay;
    private readonly IPlatformCapabilities _capabilities;
    private Dictionary<Guid, string> _accountNames = [];
    private bool _isApplyingPushRelaySnapshot;
    private bool _isSubscribedToPushRelay;

    private bool _isLoaded;
    private bool _isUpdatingSelection;

    public NotificationSettingsPageViewModel(
        IPreferencesService preferencesService,
        IAccountService accountService,
        ICalendarService calendarService,
        INotificationPolicyService notificationPolicyService,
        IUserPresenceStateProvider userPresenceStateProvider,
        IPushRelayRegistrationService pushRelay,
        IPlatformCapabilities capabilities)
    {
        PreferencesService = preferencesService;
        _pushRelay = pushRelay;
        _capabilities = capabilities;
        _accountService = accountService;
        _calendarService = calendarService;
        _notificationPolicyService = notificationPolicyService;
        _userPresenceStateProvider = userPresenceStateProvider;

        SnoozePresets = SnoozePresetOrder
            .Select(preset => new SnoozePresetOption(preset, GetSnoozePresetDisplayText(preset)))
            .ToArray();

        ScopeOptions = (MailNotificationScopeOption[])
        [
            new(MailNotificationScope.InboxOnly, Translator.MailNotificationScope_InboxOnly),
            new(MailNotificationScope.FocusedInboxOnly, Translator.MailNotificationScope_FocusedInboxOnly),
            new(MailNotificationScope.InboxAndCustomFolders, Translator.MailNotificationScope_InboxAndCustomFolders),
            new(MailNotificationScope.AllFolders, Translator.MailNotificationScope_AllFolders)
        ];

        ContentOptions = (MailNotificationContentOption[])
        [
            new(MailNotificationContent.SenderSubjectPreview, Translator.MailNotificationContent_SenderSubjectPreview),
            new(MailNotificationContent.SenderSubject, Translator.MailNotificationContent_SenderSubject),
            new(MailNotificationContent.SenderOnly, Translator.MailNotificationContent_SenderOnly),
            new(MailNotificationContent.Nothing, Translator.MailNotificationContent_Nothing)
        ];

        SoundOptions = Enum.GetValues<NotificationSoundEvent>()
            .Select(soundEvent => new NotificationSoundOption(soundEvent, GetNotificationSoundDisplayText(soundEvent)))
            .ToArray();

        QuietHoursStanceOptions = (AccountQuietHoursStanceOption[])
        [
            new(AccountQuietHoursStance.Follow, Translator.AccountQuietHoursStance_Follow),
            new(AccountQuietHoursStance.AlwaysNotify, Translator.AccountQuietHoursStance_AlwaysNotify),
            new(AccountQuietHoursStance.NeverOutsideWorkingHours, Translator.AccountQuietHoursStance_NeverOutsideWorkingHours)
        ];

        TaskReminderTimingOptions = (TaskReminderTimingOption[])
        [
            new(TaskReminderTiming.AtDueTime, Translator.TaskReminderTiming_AtDueTime),
            new(TaskReminderTiming.FifteenMinutesBefore, Translator.TaskReminderTiming_FifteenMinutesBefore),
            new(TaskReminderTiming.OneHourBefore, Translator.TaskReminderTiming_OneHourBefore)
        ];

        MailActionOptions = SupportedMailNotificationActions
            .Select(action => new MailNotificationActionOption(action, GetOperationDisplayText(action)))
            .ToArray();

        foreach (var option in CalendarReminderOptionFactory.GetReminderOptions(calendarService))
        {
            ReminderOptions.Add(option);
        }

        foreach (var option in CalendarReminderOptionFactory.GetSnoozeOptions())
        {
            CalendarSnoozeOptions.Add(option);
        }

        foreach (var minutes in TaskSnoozeMinuteOptions)
        {
            TaskSnoozeOptions.Add(string.Format(Translator.NotificationSettings_SnoozeMinutesFormat, minutes));
        }

        BuildQuietHoursDays();
        ApplyPushRelaySnapshot(_pushRelay.Snapshot);
    }

    public IPreferencesService PreferencesService { get; }

    public IReadOnlyList<SnoozePresetOption> SnoozePresets { get; }
    public MailNotificationScopeOption[] ScopeOptions { get; }
    public MailNotificationContentOption[] ContentOptions { get; }
    public NotificationSoundOption[] SoundOptions { get; }
    public AccountQuietHoursStanceOption[] QuietHoursStanceOptions { get; }
    public TaskReminderTimingOption[] TaskReminderTimingOptions { get; }
    public MailNotificationActionOption[] MailActionOptions { get; }

    public ObservableCollection<string> ReminderOptions { get; } = [];
    public ObservableCollection<string> CalendarSnoozeOptions { get; } = [];
    public ObservableCollection<string> TaskSnoozeOptions { get; } = [];
    public ObservableCollection<QuietHoursDayViewModel> QuietHoursDayToggles { get; } = [];
    public ObservableCollection<AccountNotificationSettingsViewModel> Accounts { get; } = [];

    #region Snooze

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SnoozeStateText))]
    [NotifyPropertyChangedFor(nameof(SnoozeSummaryText))]
    public partial bool IsSnoozed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SnoozeStateText))]
    [NotifyPropertyChangedFor(nameof(SnoozeSummaryText))]
    public partial DateTimeOffset? SnoozedUntil { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSnoozePresetText))]
    public partial SnoozePresetOption SelectedSnoozePreset { get; set; }

    public string SelectedSnoozePresetText => SelectedSnoozePreset?.DisplayText ?? string.Empty;

    [ObservableProperty]
    public partial bool IsSystemQuietTimeActive { get; set; }

    public string SnoozeStateText
    {
        get
        {
            if (!IsSnoozed)
                return Translator.NotificationSettings_State_Active;

            return SnoozedUntil is { } until
                ? string.Format(Translator.NotificationSettings_State_SnoozedUntilFormat, FormatTime(until))
                : Translator.NotificationSettings_State_Snoozed;
        }
    }

    public string SnoozeSummaryText
    {
        get
        {
            if (!IsSnoozed)
                return Translator.NotificationSettings_Hero_Active;

            return SnoozedUntil is { } until
                ? string.Format(Translator.NotificationSettings_Hero_SnoozedUntilFormat, FormatTime(until))
                : Translator.NotificationSettings_Hero_SnoozedIndefinitely;
        }
    }

    #endregion

    #region Per-type selections

    [ObservableProperty]
    public partial string QuietHoursSummary { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MailSummary))]
    public partial MailNotificationScopeOption SelectedMailScope { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MailSummary))]
    public partial MailNotificationContentOption SelectedMailContent { get; set; }

    [ObservableProperty]
    public partial MailNotificationActionOption SelectedFirstAction { get; set; }

    [ObservableProperty]
    public partial MailNotificationActionOption SelectedSecondAction { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedMailSoundEvent))]
    public partial NotificationSoundOption SelectedMailSound { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalendarSummary))]
    public partial int SelectedReminderIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalendarSummary))]
    public partial int SelectedCalendarSnoozeIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedCalendarSoundEvent))]
    public partial NotificationSoundOption SelectedCalendarSound { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskSummary))]
    public partial TaskReminderTimingOption SelectedTaskReminderTiming { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskSummary))]
    public partial int SelectedTaskSnoozeIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTaskSoundEvent))]
    public partial NotificationSoundOption SelectedTaskSound { get; set; }

    public string MailSummary
        => PreferencesService.AreNewMailNotificationsEnabled
            ? string.Format(
                Translator.NotificationSettings_Mail_SummaryFormat,
                SelectedMailScope?.DisplayText ?? string.Empty,
                SelectedMailContent?.DisplayText ?? string.Empty)
            : Translator.NotificationSettings_Mail_Off;

    public string CalendarSummary
        => PreferencesService.AreCalendarRemindersEnabled
            ? string.Format(
                Translator.NotificationSettings_Calendar_SummaryFormat,
                GetOptionAt(ReminderOptions, SelectedReminderIndex),
                GetOptionAt(CalendarSnoozeOptions, SelectedCalendarSnoozeIndex))
            : Translator.NotificationSettings_Calendar_Off;

    public string TaskSummary
        => PreferencesService.AreTaskRemindersEnabled
            ? string.Format(
                Translator.NotificationSettings_Tasks_SummaryFormat,
                SelectedTaskReminderTiming?.DisplayText ?? string.Empty,
                GetOptionAt(TaskSnoozeOptions, SelectedTaskSnoozeIndex))
            : Translator.NotificationSettings_Tasks_Off;

    public NotificationSoundEvent SelectedMailSoundEvent => SelectedMailSound?.Value ?? NotificationSoundEvent.Mail;
    public NotificationSoundEvent SelectedCalendarSoundEvent => SelectedCalendarSound?.Value ?? NotificationSoundEvent.Reminder;
    public NotificationSoundEvent SelectedTaskSoundEvent => SelectedTaskSound?.Value ?? NotificationSoundEvent.Default;

    #endregion

    #region Live notifications

    /// <summary>The live notifications card exists on platforms that can use the relay.</summary>
    public bool IsPushRelaySectionVisible => _capabilities.RemoteNotifications || _capabilities.PushRelayOptIn;

    /// <summary>Opt-in platforms (macOS, Windows) show a switch; iOS is always on while signed in.</summary>
    public bool IsPushRelayToggleVisible => _capabilities.PushRelayOptIn;

    /// <summary>The switch state on opt-in platforms; on iOS it mirrors whether the relay is active.</summary>
    [ObservableProperty]
    public partial bool IsPushRelayEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsPushRelayToggleEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsPushRelaySignInRequired { get; set; }

    [ObservableProperty]
    public partial string PushRelayStatusText { get; set; } = string.Empty;

    /// <summary>Accounts the relay can no longer sign in to; empty when there are none.</summary>
    [ObservableProperty]
    public partial string PushRelayMailboxWarningText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPushTestVisible { get; set; }

    /// <summary>The outcome of the last test notification request; empty until one is sent.</summary>
    [ObservableProperty]
    public partial string PushRelayTestResultText { get; set; } = string.Empty;

    public PushRelayAvailability PushRelayAvailability => _pushRelay.Snapshot.Availability;

    partial void OnIsPushRelayEnabledChanged(bool value)
    {
        if (_isApplyingPushRelaySnapshot || !IsPushRelayToggleVisible || value == _pushRelay.IsUserOptedIn)
            return;

        _ = SetPushRelayOptInAsync(value);
    }

    private async Task SetPushRelayOptInAsync(bool enabled)
    {
        try
        {
            await _pushRelay.SetUserOptInAsync(enabled);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Serilog.Log.Warning(ex, "Changing the live notification setting failed.");
        }

        await ExecuteUIThread(() => ApplyPushRelaySnapshot(_pushRelay.Snapshot));
    }

    /// <summary>The relay accepts one test per minute; the button rests for that long after a send.</summary>
    internal static readonly TimeSpan PushTestCooldown = TimeSpan.FromMinutes(1);

    private bool _isPushTestCoolingDown;

    private bool CanSendPushTest() => !_isPushTestCoolingDown;

    [RelayCommand(CanExecute = nameof(CanSendPushTest))]
    private async Task SendPushTestAsync()
    {
        PushRelayTestResultText = string.Empty;
        var result = await _pushRelay.SendPushTestAsync(CancellationToken.None);
        var text = result switch
        {
            PushRelayPushTestResult.Queued => Translator.NotificationSettings_PushRelay_TestQueued,
            PushRelayPushTestResult.NoPushToken => Translator.NotificationSettings_PushRelay_TestNoToken,
            PushRelayPushTestResult.RateLimited => Translator.NotificationSettings_PushRelay_TestRateLimited,
            PushRelayPushTestResult.NotRegistered => Translator.NotificationSettings_PushRelay_TestNotRegistered,
            _ => Translator.NotificationSettings_PushRelay_TestFailed
        };

        await ExecuteUIThread(() => PushRelayTestResultText = text);

        if (result is PushRelayPushTestResult.Queued or PushRelayPushTestResult.RateLimited)
            _ = CoolDownPushTestAsync();
    }

    private async Task CoolDownPushTestAsync()
    {
        await ExecuteUIThread(() =>
        {
            _isPushTestCoolingDown = true;
            SendPushTestCommand.NotifyCanExecuteChanged();
        });

        await Task.Delay(PushTestCooldown);

        await ExecuteUIThread(() =>
        {
            _isPushTestCoolingDown = false;
            SendPushTestCommand.NotifyCanExecuteChanged();
        });
    }

    private void PushRelaySnapshotChanged(object? sender, PushRelayRegistrationSnapshot snapshot)
        => _ = ExecuteUIThread(() => ApplyPushRelaySnapshot(snapshot));

    /// <summary>Maps the relay registration state onto the bound members. Runs on the UI thread.</summary>
    public void ApplyPushRelaySnapshot(PushRelayRegistrationSnapshot snapshot)
    {
        _isApplyingPushRelaySnapshot = true;
        try
        {
            var availability = snapshot.Availability;
            IsPushRelayEnabled = IsPushRelayToggleVisible ? snapshot.IsUserOptedIn : availability == PushRelayAvailability.Enabled;
            // Signed out, the switch can still be turned off, but not on.
            IsPushRelayToggleEnabled = availability is not (PushRelayAvailability.UnsupportedPlatform or PushRelayAvailability.SignInRequired)
                || (availability == PushRelayAvailability.SignInRequired && snapshot.IsUserOptedIn);
            IsPushRelaySignInRequired = availability == PushRelayAvailability.SignInRequired;
            IsPushTestVisible = _capabilities.RemoteNotifications && availability == PushRelayAvailability.Enabled && snapshot.DeviceId is not null;
            PushRelayStatusText = GetPushRelayStatusText(snapshot);
            PushRelayMailboxWarningText = GetPushRelayMailboxWarningText(snapshot);
            OnPropertyChanged(nameof(PushRelayAvailability));
        }
        finally
        {
            _isApplyingPushRelaySnapshot = false;
        }
    }

    private static string GetPushRelayStatusText(PushRelayRegistrationSnapshot snapshot) => snapshot.Availability switch
    {
        PushRelayAvailability.UnsupportedPlatform or PushRelayAvailability.RejectedByRelay => Translator.NotificationSettings_PushRelay_PlatformUnsupported,
        PushRelayAvailability.SignInRequired => Translator.NotificationSettings_PushRelay_SignInRequired,
        PushRelayAvailability.DisabledByUser => Translator.NotificationSettings_PushRelay_Off,
        PushRelayAvailability.ServiceUnavailable => Translator.NotificationSettings_PushRelay_ServiceUnavailable,
        _ when snapshot.Mailboxes.Any(x => !x.IsWatched && x.LastErrorCode == PushRelayErrorCodes.MailboxLimitReached)
            => Translator.NotificationSettings_PushRelay_LimitReached,
        _ when snapshot.DeviceId is null && snapshot.LastErrorCode is { } error
            => string.Format(Translator.NotificationSettings_PushRelay_ErrorFormat, error),
        _ => string.Format(Translator.NotificationSettings_PushRelay_RegisteredFormat,
            snapshot.Mailboxes.Count(x => x.IsWatched), snapshot.MailboxLimit)
    };

    private string GetPushRelayMailboxWarningText(PushRelayRegistrationSnapshot snapshot)
    {
        if (snapshot.Availability != PushRelayAvailability.Enabled)
            return string.Empty;

        var names = snapshot.Mailboxes
            .Where(x => x.NeedsReauthentication)
            .Select(x => _accountNames.TryGetValue(x.AccountId, out var name) ? name : null)
            .OfType<string>()
            .ToList();

        return names.Count == 0
            ? string.Empty
            : string.Format(Translator.NotificationSettings_PushRelay_Mailbox_NeedsReauth, string.Join(", ", names));
    }

    private void SubscribeToPushRelay()
    {
        if (_isSubscribedToPushRelay)
            return;

        _pushRelay.SnapshotChanged += PushRelaySnapshotChanged;
        _isSubscribedToPushRelay = true;
    }

    private void UnsubscribeFromPushRelay()
    {
        if (!_isSubscribedToPushRelay)
            return;

        _pushRelay.SnapshotChanged -= PushRelaySnapshotChanged;
        _isSubscribedToPushRelay = false;
    }

    #endregion

    public override async void OnNavigatedTo(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedTo(mode, parameters);

        LoadSelections();
        RefreshSnoozeState();
        UpdateSectionSummaries();
        SubscribeToPushRelay();
        PushRelayTestResultText = string.Empty;
        ApplyPushRelaySnapshot(_pushRelay.Snapshot);

        await LoadAccountsAsync();
        await ExecuteUIThread(() => ApplyPushRelaySnapshot(_pushRelay.Snapshot));
    }

    public override void OnNavigatedFrom(NavigationMode mode, object? parameters)
    {
        base.OnNavigatedFrom(mode, parameters);

        UnsubscribeFromPushRelay();
    }

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

    /// <summary>
    /// Re-reads the snooze state. Expiry is evaluated lazily rather than scheduled, so the page calls
    /// this on a timer to keep the countdown text and the toggle honest while it is open.
    /// </summary>
    public void RefreshSnoozeState()
    {
        var state = _notificationPolicyService.GetSnoozeState(DateTimeOffset.Now);

        IsSnoozed = state.IsSnoozed;
        SnoozedUntil = state.Until;
        IsSystemQuietTimeActive = _userPresenceStateProvider.IsSystemQuietTimeActive();

        var preset = state.IsSnoozed ? state.Preset : PreferencesService.LastUsedSnoozePreset;
        var match = SnoozePresets.FirstOrDefault(option => option.Value == preset);

        if (match is not null && !ReferenceEquals(match, SelectedSnoozePreset))
        {
            SelectedSnoozePreset = match;
        }
    }

    /// <summary>
    /// Rebuilds every one-line expander summary. Cheap, and simpler than tracking which preference
    /// feeds which string.
    /// </summary>
    public void UpdateSectionSummaries()
    {
        UpdateQuietHoursSummary();

        OnPropertyChanged(nameof(MailSummary));
        OnPropertyChanged(nameof(CalendarSummary));
        OnPropertyChanged(nameof(TaskSummary));
    }

    [RelayCommand]
    private void StartSnooze(NotificationSnoozePreset preset)
    {
        _notificationPolicyService.StartSnooze(preset, customUntilLocal: null, DateTimeOffset.Now);

        RefreshSnoozeState();
    }

    [RelayCommand]
    private void EndSnooze()
    {
        _notificationPolicyService.EndSnooze();

        RefreshSnoozeState();
    }

    /// <summary>
    /// Starts a snooze ending at an arbitrary local time, the one duration the companion dropdown
    /// cannot offer.
    /// </summary>
    public void StartCustomSnooze(DateTimeOffset untilLocal)
    {
        _notificationPolicyService.StartSnooze(NotificationSnoozePreset.Custom, untilLocal, DateTimeOffset.Now);

        RefreshSnoozeState();
    }

    /// <summary>
    /// The Reset button (Windows NotificationSettingsPage): asks first, then <see cref="ResetAsync"/>. True when the
    /// preferences were reset, so a head can refresh what it keeps outside the ViewModel (the Mac alert sounds).
    /// </summary>
    public async Task<bool> ConfirmResetAsync(IDialogServiceBase dialogService)
    {
        ArgumentNullException.ThrowIfNull(dialogService);
        if (!await dialogService.ShowConfirmationDialogAsync(Translator.NotificationSettings_Reset_ConfirmMessage,
                Translator.NotificationSettings_Reset_ConfirmTitle, Translator.NotificationSettings_Reset_Title))
            return false;
        await ResetAsync();
        return true;
    }

    /// <summary>
    /// Returns every notification preference and account override to its factory value. The live notification
    /// opt-in is a privacy choice, not a notification default, so it is left alone.
    /// </summary>
    public async Task ResetAsync()
    {
        _notificationPolicyService.EndSnooze();

        PreferencesService.LastUsedSnoozePreset = NotificationSnoozePreset.OneHour;
        PreferencesService.AreQuietHoursEnabled = false;
        PreferencesService.QuietHoursStart = new TimeSpan(18, 30, 0);
        PreferencesService.QuietHoursEnd = new TimeSpan(8, 0, 0);
        PreferencesService.QuietHoursDays = QuietHoursDays.Weekdays;
        PreferencesService.SnoozeWhilePresenting = false;

        PreferencesService.AreNewMailNotificationsEnabled = true;
        PreferencesService.MailNotificationScope = MailNotificationScope.InboxOnly;
        PreferencesService.MailNotificationContent = MailNotificationContent.SenderSubjectPreview;
        PreferencesService.FirstMailNotificationAction = MailOperation.MarkAsRead;
        PreferencesService.SecondMailNotificationAction = MailOperation.SoftDelete;
        PreferencesService.MailNotificationSoundEvent = NotificationSoundEvent.Mail;

        PreferencesService.AreCalendarRemindersEnabled = true;
        PreferencesService.CalendarNotificationSoundEvent = NotificationSoundEvent.Reminder;

        PreferencesService.AreTaskRemindersEnabled = true;
        PreferencesService.TaskReminderTiming = TaskReminderTiming.AtDueTime;
        PreferencesService.TaskReminderSnoozeMinutes = 60;
        PreferencesService.TaskNotificationSoundEvent = NotificationSoundEvent.Default;

        foreach (var accountViewModel in Accounts)
        {
            var account = await _accountService.GetAccountAsync(accountViewModel.AccountId);

            if (account?.Preferences == null)
                continue;

            account.Preferences.IsNotificationsEnabled = true;
            account.Preferences.HasCustomNotificationSettings = false;
            account.Preferences.QuietHoursStance = AccountQuietHoursStance.Follow;

            await _accountService.UpdateAccountAsync(account);
        }

        LoadSelections();
        RefreshSnoozeState();
        UpdateSectionSummaries();

        await LoadAccountsAsync();
    }

    private async Task LoadAccountsAsync()
    {
        var accounts = await _accountService.GetAccountsAsync();
        var names = accounts.ToDictionary(
            account => account.Id,
            account => string.IsNullOrWhiteSpace(account.Name) ? account.Address ?? string.Empty : account.Name);

        await ExecuteUIThread(() =>
        {
            _accountNames = names;
            Accounts.Clear();

            foreach (var account in accounts)
            {
                if (!account.IsMailAccessGranted || account.Preferences == null)
                    continue;

                Accounts.Add(new AccountNotificationSettingsViewModel(
                    account,
                    ScopeOptions,
                    ContentOptions,
                    SoundOptions,
                    QuietHoursStanceOptions,
                    SaveAccountAsync));
            }
        });
    }

    private async Task SaveAccountAsync(AccountNotificationSettingsViewModel accountViewModel)
    {
        var account = await _accountService.GetAccountAsync(accountViewModel.AccountId);

        if (account?.Preferences == null)
            return;

        account.Preferences.IsNotificationsEnabled = accountViewModel.AreNotificationsEnabled;
        account.Preferences.HasCustomNotificationSettings = accountViewModel.HasCustomNotificationSettings;
        account.Preferences.IsNewMailNotificationEnabled = accountViewModel.IsNewMailNotificationEnabled;
        account.Preferences.AreCalendarRemindersEnabled = accountViewModel.AreCalendarRemindersEnabled;
        account.Preferences.NotificationScope = accountViewModel.SelectedScope?.Value ?? MailNotificationScope.InboxOnly;
        account.Preferences.NotificationContent = accountViewModel.SelectedContent?.Value ?? MailNotificationContent.SenderSubjectPreview;
        account.Preferences.AccountNotificationSoundEvent = accountViewModel.SelectedSound?.Value ?? NotificationSoundEvent.Mail;
        account.Preferences.QuietHoursStance = accountViewModel.SelectedQuietHoursStance?.Value ?? AccountQuietHoursStance.Follow;

        await _accountService.UpdateAccountAsync(account);
    }

    private void LoadSelections()
    {
        _isLoaded = false;

        SelectedMailScope = NotificationOptionLookup.Find(ScopeOptions, PreferencesService.MailNotificationScope, option => option.Value);
        SelectedMailContent = NotificationOptionLookup.Find(ContentOptions, PreferencesService.MailNotificationContent, option => option.Value);
        SelectedMailSound = NotificationOptionLookup.Find(SoundOptions, PreferencesService.MailNotificationSoundEvent, option => option.Value);

        var firstAction = ResolveSupportedAction(PreferencesService.FirstMailNotificationAction, MailOperation.MarkAsRead);
        var secondAction = ResolveSupportedAction(PreferencesService.SecondMailNotificationAction, MailOperation.SoftDelete);

        if (secondAction == firstAction)
        {
            secondAction = GetFallbackDistinctAction(firstAction);
        }

        SelectedFirstAction = NotificationOptionLookup.Find(MailActionOptions, firstAction, option => option.Operation);
        SelectedSecondAction = NotificationOptionLookup.Find(MailActionOptions, secondAction, option => option.Operation);

        PreferencesService.FirstMailNotificationAction = firstAction;
        PreferencesService.SecondMailNotificationAction = secondAction;

        SelectedReminderIndex = CalendarReminderOptionFactory.GetSelectedReminderIndex(
            _calendarService,
            PreferencesService.DefaultReminderDurationInSeconds);
        SelectedCalendarSnoozeIndex = CalendarReminderOptionFactory.GetSelectedSnoozeIndex(PreferencesService.DefaultSnoozeDurationInMinutes);
        SelectedCalendarSound = NotificationOptionLookup.Find(SoundOptions, PreferencesService.CalendarNotificationSoundEvent, option => option.Value);

        SelectedTaskReminderTiming = NotificationOptionLookup.Find(TaskReminderTimingOptions, PreferencesService.TaskReminderTiming, option => option.Value);
        SelectedTaskSnoozeIndex = Math.Max(0, Array.IndexOf(TaskSnoozeMinuteOptions, PreferencesService.TaskReminderSnoozeMinutes));
        SelectedTaskSound = NotificationOptionLookup.Find(SoundOptions, PreferencesService.TaskNotificationSoundEvent, option => option.Value);

        var days = PreferencesService.QuietHoursDays;

        foreach (var toggle in QuietHoursDayToggles)
        {
            toggle.IsSelected = days.HasFlag(ToQuietHoursDay(toggle.Day));
        }

        _isLoaded = true;
    }

    /// <summary>
    /// Builds the seven day toggles in the culture's own week order, labelled from the culture's
    /// abbreviated day names so no extra translation resources are needed.
    /// </summary>
    private void BuildQuietHoursDays()
    {
        var culture = new CultureInfo(WinoTranslationDictionary.GetLanguageFileNameRelativePath(PreferencesService.CurrentLanguage));
        var firstDay = (int)PreferencesService.FirstDayOfWeek;

        for (var offset = 0; offset < 7; offset++)
        {
            var day = (DayOfWeek)((firstDay + offset) % 7);

            QuietHoursDayToggles.Add(new QuietHoursDayViewModel(
                day,
                culture.DateTimeFormat.AbbreviatedDayNames[(int)day],
                SaveQuietHoursDays));
        }
    }

    partial void OnSelectedMailScopeChanged(MailNotificationScopeOption value)
    {
        if (_isLoaded && value is not null)
        {
            PreferencesService.MailNotificationScope = value.Value;
        }
    }

    partial void OnSelectedMailContentChanged(MailNotificationContentOption value)
    {
        if (_isLoaded && value is not null)
        {
            PreferencesService.MailNotificationContent = value.Value;
        }
    }

    partial void OnSelectedMailSoundChanged(NotificationSoundOption value)
    {
        if (_isLoaded && value is not null)
        {
            PreferencesService.MailNotificationSoundEvent = value.Value;
        }
    }

    partial void OnSelectedFirstActionChanged(MailNotificationActionOption value)
    {
        if (!_isLoaded || _isUpdatingSelection || value is null)
            return;

        EnsureDistinctSelections(value.Operation, isFirstSelection: true);
        PreferencesService.FirstMailNotificationAction = value.Operation;
    }

    partial void OnSelectedSecondActionChanged(MailNotificationActionOption value)
    {
        if (!_isLoaded || _isUpdatingSelection || value is null)
            return;

        EnsureDistinctSelections(value.Operation, isFirstSelection: false);
        PreferencesService.SecondMailNotificationAction = value.Operation;
    }

    partial void OnSelectedReminderIndexChanged(int value)
    {
        if (_isLoaded)
        {
            PreferencesService.DefaultReminderDurationInSeconds = CalendarReminderOptionFactory.GetReminderDurationInSeconds(_calendarService, value);
        }
    }

    partial void OnSelectedCalendarSnoozeIndexChanged(int value)
    {
        if (_isLoaded && CalendarReminderOptionFactory.GetSnoozeMinutes(value) is { } minutes)
        {
            PreferencesService.DefaultSnoozeDurationInMinutes = minutes;
        }
    }

    partial void OnSelectedCalendarSoundChanged(NotificationSoundOption value)
    {
        if (_isLoaded && value is not null)
        {
            PreferencesService.CalendarNotificationSoundEvent = value.Value;
        }
    }

    partial void OnSelectedTaskReminderTimingChanged(TaskReminderTimingOption value)
    {
        if (_isLoaded && value is not null)
        {
            PreferencesService.TaskReminderTiming = value.Value;
        }
    }

    partial void OnSelectedTaskSnoozeIndexChanged(int value)
    {
        if (_isLoaded && value >= 0 && value < TaskSnoozeMinuteOptions.Length)
        {
            PreferencesService.TaskReminderSnoozeMinutes = TaskSnoozeMinuteOptions[value];
        }
    }

    partial void OnSelectedTaskSoundChanged(NotificationSoundOption value)
    {
        if (_isLoaded && value is not null)
        {
            PreferencesService.TaskNotificationSoundEvent = value.Value;
        }
    }

    private void SaveQuietHoursDays()
    {
        if (!_isLoaded)
            return;

        var days = QuietHoursDays.None;

        foreach (var toggle in QuietHoursDayToggles.Where(toggle => toggle.IsSelected))
        {
            days |= ToQuietHoursDay(toggle.Day);
        }

        PreferencesService.QuietHoursDays = days;

        UpdateQuietHoursSummary();
    }

    /// <summary>
    /// Rebuilds the one-line quiet hours description shown on the collapsed expander.
    /// </summary>
    public void UpdateQuietHoursSummary()
    {
        if (!PreferencesService.AreQuietHoursEnabled)
        {
            QuietHoursSummary = Translator.NotificationSettings_QuietHours_Off;
            return;
        }

        QuietHoursSummary = string.Format(
            Translator.NotificationSettings_QuietHours_SummaryFormat,
            GetQuietHoursDaysText(),
            FormatTimeOfDay(PreferencesService.QuietHoursStart),
            FormatTimeOfDay(PreferencesService.QuietHoursEnd));
    }

    private string GetQuietHoursDaysText()
    {
        var days = PreferencesService.QuietHoursDays;

        if (days == QuietHoursDays.All) return Translator.NotificationSettings_QuietHours_EveryDay;
        if (days == QuietHoursDays.Weekdays) return Translator.NotificationSettings_QuietHours_Weekdays;
        if (days == QuietHoursDays.Weekend) return Translator.NotificationSettings_QuietHours_Weekend;
        if (days == QuietHoursDays.None) return Translator.NotificationSettings_QuietHours_NoDays;

        return string.Join(", ", QuietHoursDayToggles.Where(toggle => toggle.IsSelected).Select(toggle => toggle.DisplayText));
    }

    public void Receive(PreferenceChangedMessage message)
    {
        if (message.PropertyName is not (nameof(IPreferencesService.NotificationSnoozePreset)
            or nameof(IPreferencesService.NotificationSnoozeUntilUtcTicks)))
        {
            return;
        }

        // The write can come from a background sync thread, so hop before touching bound state.
        _ = ExecuteUIThread(RefreshSnoozeState);
    }

    private void EnsureDistinctSelections(MailOperation changedAction, bool isFirstSelection)
    {
        var otherSelection = isFirstSelection ? SelectedSecondAction : SelectedFirstAction;

        if (otherSelection?.Operation != changedAction)
            return;

        _isUpdatingSelection = true;

        var fallbackAction = GetFallbackDistinctAction(changedAction);
        var fallbackOption = NotificationOptionLookup.Find(MailActionOptions, fallbackAction, option => option.Operation);

        if (isFirstSelection)
        {
            SelectedSecondAction = fallbackOption;
            PreferencesService.SecondMailNotificationAction = fallbackAction;
        }
        else
        {
            SelectedFirstAction = fallbackOption;
            PreferencesService.FirstMailNotificationAction = fallbackAction;
        }

        _isUpdatingSelection = false;
    }

    private static QuietHoursDays ToQuietHoursDay(DayOfWeek day)
        => day switch
        {
            DayOfWeek.Monday => QuietHoursDays.Monday,
            DayOfWeek.Tuesday => QuietHoursDays.Tuesday,
            DayOfWeek.Wednesday => QuietHoursDays.Wednesday,
            DayOfWeek.Thursday => QuietHoursDays.Thursday,
            DayOfWeek.Friday => QuietHoursDays.Friday,
            DayOfWeek.Saturday => QuietHoursDays.Saturday,
            _ => QuietHoursDays.Sunday
        };

    private static string GetOptionAt(IList<string> options, int index)
        => index >= 0 && index < options.Count ? options[index] : string.Empty;

    private static string FormatTime(DateTimeOffset value) => value.ToString("t");

    private static string FormatTimeOfDay(TimeSpan value) => DateTime.Today.Add(value).ToString("t");

    private static MailOperation ResolveSupportedAction(MailOperation action, MailOperation fallbackAction)
        => SupportedMailNotificationActions.Contains(action) ? action : fallbackAction;

    private static MailOperation GetFallbackDistinctAction(MailOperation excludedAction)
        => SupportedMailNotificationActions.First(action => action != excludedAction);

    private static string GetSnoozePresetDisplayText(NotificationSnoozePreset preset)
        => preset switch
        {
            NotificationSnoozePreset.ThirtyMinutes => Translator.NotificationSnooze_ThirtyMinutes,
            NotificationSnoozePreset.OneHour => Translator.NotificationSnooze_OneHour,
            NotificationSnoozePreset.TwoHours => Translator.NotificationSnooze_TwoHours,
            NotificationSnoozePreset.RestOfDay => Translator.NotificationSnooze_RestOfDay,
            NotificationSnoozePreset.UntilTomorrowMorning => Translator.NotificationSnooze_UntilTomorrowMorning,
            NotificationSnoozePreset.UntilTurnedBackOn => Translator.NotificationSnooze_UntilTurnedBackOn,
            NotificationSnoozePreset.Custom => Translator.NotificationSnooze_Custom,
            _ => preset.ToString()
        };

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
}
