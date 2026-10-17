namespace Wino.Core.Domain;

public static class Constants
{
    /// <summary>
    /// MIME header that exists in all the drafts created from Wino.
    /// </summary>
    public const string WinoLocalDraftHeader = "X-Draft-Id";

    /// <summary>
    /// Draft mapping header used before <see cref="WinoLocalDraftHeader"/>. Drafts created by older versions still carry it.
    /// </summary>
    public const string LegacyWinoLocalDraftHeader = "X-Wino-Draft-Id";

    public static bool IsLocalDraftHeader(string headerName)
        => string.Equals(headerName, WinoLocalDraftHeader, System.StringComparison.OrdinalIgnoreCase)
           || string.Equals(headerName, LegacyWinoLocalDraftHeader, System.StringComparison.OrdinalIgnoreCase);
    public const string DispositionNotificationToHeader = "Disposition-Notification-To";
    public const string OriginalMessageIdHeader = "Original-Message-ID";
    public const string LocalDraftStartPrefix = "localDraft_";

    public const string CalendarEventRecurrenceRuleSeperator = "___";

    public const string ToastMailUniqueIdKey = nameof(ToastMailUniqueIdKey);

    /// <summary>Relay alerts enriched by the iOS Notification Service Extension: the provider's message id and the local account id.</summary>
    public const string ToastRelayMailIdKey = "wino.mailId";
    public const string ToastRelayAccountIdKey = "wino.accountId";

    /// <summary>Enriched relay alerts: the RFC Message-ID, a lookup that works whatever id type the provider used.</summary>
    public const string ToastRelayInternetMessageIdKey = "wino.messageId";

    /// <summary>Every relay push: the local account id (<c>cid</c>), present even when the alert was not enriched.</summary>
    public const string ToastRelayClientAccountIdKey = "cid";
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
    public const string ToastStoreUpdateActionKey = nameof(ToastStoreUpdateActionKey);
    public const string ToastStoreUpdateActionInstall = nameof(ToastStoreUpdateActionInstall);
    public const string StoreUpdateNotificationSuppressionKey = nameof(StoreUpdateNotificationSuppressionKey);

    /// <summary>Mail synchronization interval in minutes when the user has not chosen one, or the platform has no choice.</summary>
    public const int DefaultEmailSyncIntervalMinutes = 3;

    /// <summary>Calendar synchronization interval in minutes when the user has not chosen one, or the platform has no choice.</summary>
    public const int DefaultCalendarSyncIntervalMinutes = 5;

    /// <summary>Mail and calendar interval in minutes while relay-based sync is selected but the relay socket is down.</summary>
    public const int RelayFallbackSyncIntervalMinutes = 5;
    public const string JumpListActionKey = "JumpListAction";
    public const string JumpListOpenMailFolderAction = "OpenMailFolder";
    public const string JumpListAccountIdKey = "JumpListAccountId";
    public const string JumpListFolderIdKey = "JumpListFolderId";
    public const string ClientLogFile = "Client_.log";
    public const string ServerLogFile = "Server_.log";
    public const string LogArchiveFileName = "WinoLogs.zip";

    public const string WinoMailIdentiifer = nameof(WinoMailIdentiifer);
    public const string WinoCalendarIdentifier = nameof(WinoCalendarIdentifier);

    /// <summary>
    /// How many mail accounts can be added without the Unlimited Accounts add-on.
    /// </summary>
    public const int FreeAccountLimit = 3;
}

