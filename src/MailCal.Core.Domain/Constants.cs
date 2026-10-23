namespace MailCal.Core.Domain;

public static class Constants
{
    /// <summary>
    /// MIME header that exists in all the drafts created from Mail-Cal.
    /// </summary>
    public const string MailCalLocalDraftHeader = "X-Draft-Id";

    /// <summary>
    /// Draft mapping header used before <see cref="MailCalLocalDraftHeader"/>. Drafts created by older versions still carry it.
    /// </summary>
    public const string LegacyLocalDraftHeader = "X-Wino-Draft-Id";

    public static bool IsLocalDraftHeader(string headerName)
        => string.Equals(headerName, MailCalLocalDraftHeader, System.StringComparison.OrdinalIgnoreCase)
           || string.Equals(headerName, LegacyLocalDraftHeader, System.StringComparison.OrdinalIgnoreCase);
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
    public const string ToastModePeople = nameof(ToastModePeople);
    public const string ToastModeTasks = nameof(ToastModeTasks);
    public const string ToastDismissActionKey = nameof(ToastDismissActionKey);
    public const string JumpListActionKey = "JumpListAction";
    public const string JumpListOpenMailFolderAction = "OpenMailFolder";
    public const string JumpListAccountIdKey = "JumpListAccountId";
    public const string JumpListFolderIdKey = "JumpListFolderId";
    public const string ClientLogFile = "Client_.log";
    public const string ServerLogFile = "Server_.log";
    public const string LogArchiveFileName = "MailCalLogs.zip";

    public const string MailCalIdentiifer = nameof(MailCalIdentiifer);
    public const string MailCalCalendarIdentifier = nameof(MailCalCalendarIdentifier);

}

