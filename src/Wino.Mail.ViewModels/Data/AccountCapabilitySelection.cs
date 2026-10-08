using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.ViewModels.Data;

/// <summary>
/// What an account is used for: one mode per capability (mail, calendar, contacts, To Do).
/// The account wizard and the IMAP server settings page share this model through the
/// AccountCapabilityPicker control, so both apply the same availability and fallback rules.
/// </summary>
public partial class AccountCapabilitySelection : ObservableObject
{
    // Turning a capability off and on again restores the mode it had, not the default.
    private AccountCapabilityMode _lastCalendarMode = AccountCapabilityMode.Provider;
    private AccountCapabilityMode _lastContactMode = AccountCapabilityMode.Provider;
    private AccountCapabilityMode _lastTaskMode = AccountCapabilityMode.Local;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMailEnabled))]
    [NotifyPropertyChangedFor(nameof(HasAnyCapability))]
    [NotifyPropertyChangedFor(nameof(IsSelectionMissing))]
    [NotifyPropertyChangedFor(nameof(RequiresRemoteService))]
    public partial AccountCapabilityMode MailMode { get; set; } = AccountCapabilityMode.Provider;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCalendarEnabled))]
    [NotifyPropertyChangedFor(nameof(IsCalendarProviderSelected))]
    [NotifyPropertyChangedFor(nameof(IsCalendarLocalSelected))]
    [NotifyPropertyChangedFor(nameof(IsCalendarModeChoiceVisible))]
    [NotifyPropertyChangedFor(nameof(IsCalendarLocalOnlyNoteVisible))]
    [NotifyPropertyChangedFor(nameof(HasAnyCapability))]
    [NotifyPropertyChangedFor(nameof(IsSelectionMissing))]
    [NotifyPropertyChangedFor(nameof(RequiresRemoteService))]
    public partial AccountCapabilityMode CalendarMode { get; set; } = AccountCapabilityMode.Provider;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContactEnabled))]
    [NotifyPropertyChangedFor(nameof(IsContactProviderSelected))]
    [NotifyPropertyChangedFor(nameof(IsContactLocalSelected))]
    [NotifyPropertyChangedFor(nameof(IsContactModeChoiceVisible))]
    [NotifyPropertyChangedFor(nameof(IsContactLocalOnlyNoteVisible))]
    [NotifyPropertyChangedFor(nameof(HasAnyCapability))]
    [NotifyPropertyChangedFor(nameof(IsSelectionMissing))]
    [NotifyPropertyChangedFor(nameof(RequiresRemoteService))]
    public partial AccountCapabilityMode ContactMode { get; set; } = AccountCapabilityMode.Provider;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTaskEnabled))]
    [NotifyPropertyChangedFor(nameof(IsTaskProviderSelected))]
    [NotifyPropertyChangedFor(nameof(IsTaskLocalSelected))]
    [NotifyPropertyChangedFor(nameof(IsTaskModeChoiceVisible))]
    [NotifyPropertyChangedFor(nameof(IsTaskLocalOnlyNoteVisible))]
    [NotifyPropertyChangedFor(nameof(HasAnyCapability))]
    [NotifyPropertyChangedFor(nameof(IsSelectionMissing))]
    [NotifyPropertyChangedFor(nameof(RequiresRemoteService))]
    public partial AccountCapabilityMode TaskMode { get; set; } = AccountCapabilityMode.Local;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCalendarModeChoiceVisible))]
    [NotifyPropertyChangedFor(nameof(IsCalendarLocalOnlyNoteVisible))]
    public partial bool IsCalendarProviderModeAvailable { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContactModeChoiceVisible))]
    [NotifyPropertyChangedFor(nameof(IsContactLocalOnlyNoteVisible))]
    public partial bool IsContactProviderModeAvailable { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTaskModeChoiceVisible))]
    [NotifyPropertyChangedFor(nameof(IsTaskLocalOnlyNoteVisible))]
    public partial bool IsTaskProviderModeAvailable { get; set; }

    /// <summary>
    /// The service the provider mode syncs with, for example "Outlook" or "CalDAV".
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CalendarProviderOptionText))]
    [NotifyPropertyChangedFor(nameof(CalendarProviderOptionDescription))]
    public partial string CalendarProviderName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContactProviderOptionText))]
    [NotifyPropertyChangedFor(nameof(ContactProviderOptionDescription))]
    public partial string ContactProviderName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskProviderOptionText))]
    [NotifyPropertyChangedFor(nameof(TaskProviderOptionDescription))]
    public partial string TaskProviderName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MailDescription { get; set; } = Translator.ProviderSelection_MailStepDescription;

    /// <summary>
    /// Shown under To Do when the provider has no task service.
    /// </summary>
    [ObservableProperty]
    public partial string TaskLocalOnlyNote { get; set; } = Translator.ProviderSelection_TaskProviderUnavailable;

    public bool IsMailEnabled
    {
        get => MailMode != AccountCapabilityMode.Off;
        set => MailMode = value ? AccountCapabilityMode.Provider : AccountCapabilityMode.Off;
    }

    public bool IsCalendarEnabled
    {
        get => CalendarMode != AccountCapabilityMode.Off;
        set => CalendarMode = value ? RestoreMode(_lastCalendarMode, IsCalendarProviderModeAvailable) : AccountCapabilityMode.Off;
    }

    public bool IsContactEnabled
    {
        get => ContactMode != AccountCapabilityMode.Off;
        set => ContactMode = value ? RestoreMode(_lastContactMode, IsContactProviderModeAvailable) : AccountCapabilityMode.Off;
    }

    public bool IsTaskEnabled
    {
        get => TaskMode != AccountCapabilityMode.Off;
        set => TaskMode = value ? RestoreMode(_lastTaskMode, IsTaskProviderModeAvailable) : AccountCapabilityMode.Off;
    }

    // Radio buttons write false to the option they leave, so only a true value selects a mode.
    public bool IsCalendarProviderSelected
    {
        get => CalendarMode == AccountCapabilityMode.Provider;
        set { if (value) CalendarMode = AccountCapabilityMode.Provider; }
    }

    public bool IsCalendarLocalSelected
    {
        get => CalendarMode == AccountCapabilityMode.Local;
        set { if (value) CalendarMode = AccountCapabilityMode.Local; }
    }

    public bool IsContactProviderSelected
    {
        get => ContactMode == AccountCapabilityMode.Provider;
        set { if (value) ContactMode = AccountCapabilityMode.Provider; }
    }

    public bool IsContactLocalSelected
    {
        get => ContactMode == AccountCapabilityMode.Local;
        set { if (value) ContactMode = AccountCapabilityMode.Local; }
    }

    public bool IsTaskProviderSelected
    {
        get => TaskMode == AccountCapabilityMode.Provider;
        set { if (value) TaskMode = AccountCapabilityMode.Provider; }
    }

    public bool IsTaskLocalSelected
    {
        get => TaskMode == AccountCapabilityMode.Local;
        set { if (value) TaskMode = AccountCapabilityMode.Local; }
    }

    public bool IsCalendarModeChoiceVisible => IsCalendarProviderModeAvailable;
    public bool IsCalendarLocalOnlyNoteVisible => !IsCalendarProviderModeAvailable;
    public bool IsContactModeChoiceVisible => IsContactProviderModeAvailable;
    public bool IsContactLocalOnlyNoteVisible => !IsContactProviderModeAvailable;
    public bool IsTaskModeChoiceVisible => IsTaskProviderModeAvailable;
    public bool IsTaskLocalOnlyNoteVisible => !IsTaskProviderModeAvailable;

    public string CalendarProviderOptionText => string.Format(Translator.CapabilityPicker_SyncWith, CalendarProviderName);
    public string ContactProviderOptionText => string.Format(Translator.CapabilityPicker_SyncWith, ContactProviderName);
    public string TaskProviderOptionText => string.Format(Translator.CapabilityPicker_SyncWith, TaskProviderName);

    public string CalendarProviderOptionDescription => string.Format(Translator.ProviderSelection_Why_CalendarProvider, CalendarProviderName);
    public string ContactProviderOptionDescription => string.Format(Translator.ProviderSelection_Why_ContactsProvider, ContactProviderName);
    public string TaskProviderOptionDescription => string.Format(Translator.ProviderSelection_Why_TasksProvider, TaskProviderName);

    public bool HasAnyCapability =>
        MailMode != AccountCapabilityMode.Off ||
        CalendarMode != AccountCapabilityMode.Off ||
        ContactMode != AccountCapabilityMode.Off ||
        TaskMode != AccountCapabilityMode.Off;

    public bool IsSelectionMissing => !HasAnyCapability;

    /// <summary>
    /// True when at least one capability talks to a server, so the account needs a sign-in.
    /// An account with only local capabilities needs none.
    /// </summary>
    public bool RequiresRemoteService =>
        MailMode != AccountCapabilityMode.Off ||
        CalendarMode == AccountCapabilityMode.Provider ||
        ContactMode == AccountCapabilityMode.Provider ||
        TaskMode == AccountCapabilityMode.Provider;

    /// <summary>
    /// Sets which capabilities the provider can serve and falls back to the local mode for the
    /// ones it cannot, so an unavailable option is never the selected one.
    /// </summary>
    public void ConfigureProvider(bool isCalendarProviderAvailable,
                                  bool isContactProviderAvailable,
                                  bool isTaskProviderAvailable,
                                  string calendarProviderName,
                                  string contactProviderName,
                                  string taskProviderName)
    {
        IsCalendarProviderModeAvailable = isCalendarProviderAvailable;
        IsContactProviderModeAvailable = isContactProviderAvailable;
        IsTaskProviderModeAvailable = isTaskProviderAvailable;
        CalendarProviderName = calendarProviderName ?? string.Empty;
        ContactProviderName = contactProviderName ?? string.Empty;
        TaskProviderName = taskProviderName ?? string.Empty;

        CoerceUnavailableModes();
    }

    public void CoerceUnavailableModes()
    {
        if (!IsCalendarProviderModeAvailable && CalendarMode == AccountCapabilityMode.Provider)
            CalendarMode = AccountCapabilityMode.Local;

        if (!IsContactProviderModeAvailable && ContactMode == AccountCapabilityMode.Provider)
            ContactMode = AccountCapabilityMode.Local;

        if (!IsTaskProviderModeAvailable && TaskMode == AccountCapabilityMode.Provider)
            TaskMode = AccountCapabilityMode.Local;
    }

    partial void OnCalendarModeChanged(AccountCapabilityMode value)
    {
        if (value != AccountCapabilityMode.Off)
            _lastCalendarMode = value;
    }

    partial void OnContactModeChanged(AccountCapabilityMode value)
    {
        if (value != AccountCapabilityMode.Off)
            _lastContactMode = value;
    }

    partial void OnTaskModeChanged(AccountCapabilityMode value)
    {
        if (value != AccountCapabilityMode.Off)
            _lastTaskMode = value;
    }

    private static AccountCapabilityMode RestoreMode(AccountCapabilityMode lastMode, bool isProviderAvailable)
        => lastMode == AccountCapabilityMode.Provider && !isProviderAvailable
            ? AccountCapabilityMode.Local
            : lastMode;
}
