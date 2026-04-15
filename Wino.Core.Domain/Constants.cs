namespace Wino.Core.Domain;

public static class Constants
{
    /// <summary>
    /// MIME header that exists in all the drafts created from Wino.
    /// </summary>
    public const string WinoLocalDraftHeader = "X-Wino-Draft-Id";
    public const string DispositionNotificationToHeader = "Disposition-Notification-To";
    public const string OriginalMessageIdHeader = "Original-Message-ID";
    public const string LocalDraftStartPrefix = "localDraft_";

    public const string CalendarEventRecurrenceRuleSeperator = "___";

    public const string ToastMailUniqueIdKey = nameof(ToastMailUniqueIdKey);
    public const string ToastActionKey = nameof(ToastActionKey);
    public const string ToastMailAccountIdKey = nameof(ToastMailAccountIdKey);
    public const string ToastCalendarItemIdKey = nameof(ToastCalendarItemIdKey);
    public const string ToastCalendarActionKey = nameof(ToastCalendarActionKey);
    public const string ToastCalendarNavigateAction = nameof(ToastCalendarNavigateAction);
    public const string ToastCalendarJoinOnlineAction = nameof(ToastCalendarJoinOnlineAction);
    public const string ToastCalendarSnoozeAction = nameof(ToastCalendarSnoozeAction);
    public const string ToastCalendarSnoozeDurationInputId = nameof(ToastCalendarSnoozeDurationInputId);
    public const string ToastModeKey = nameof(ToastModeKey);
    public const string ToastModeMail = nameof(ToastModeMail);
    public const string ToastModeCalendar = nameof(ToastModeCalendar);
    public const string ToastDismissActionKey = nameof(ToastDismissActionKey);
    public const string ToastStoreUpdateActionKey = nameof(ToastStoreUpdateActionKey);
    public const string ToastStoreUpdateActionInstall = nameof(ToastStoreUpdateActionInstall);
    public const string ClientLogFile = "Client_.log";
    public const string ServerLogFile = "Server_.log";
    public const string LogArchiveFileName = "WinoLogs.zip";

    public const string WinoMailIdentiifer = nameof(WinoMailIdentiifer);
    public const string WinoCalendarIdentifier = nameof(WinoCalendarIdentifier);
}

