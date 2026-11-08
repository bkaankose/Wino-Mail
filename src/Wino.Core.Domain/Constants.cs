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
    public const long MaximumLocalAttachmentSizeBytes = 150L * 1024 * 1024;

    /// <summary>Calendar synchronization interval in minutes when the user has not chosen one, or the platform has no choice.</summary>
    public const int DefaultCalendarSyncIntervalMinutes = 5;
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

    /// <summary>
    /// Attachment extensions that can run code when opened. Lower-case, with the dot.
    /// </summary>
    public static readonly string[] ExecutableAttachmentExtensions =
    [
        ".exe", ".msi", ".msix", ".bat", ".cmd", ".ps1", ".js", ".jse", ".vbs", ".vbe", ".wsf",
        ".scr", ".lnk", ".hta", ".iso", ".img", ".reg", ".com", ".pif", ".cpl", ".jar", ".appx"
    ];

    /// <summary>
    /// Query keys that start with this prefix are tracking parameters.
    /// </summary>
    public const string LinkTrackingParameterPrefix = "utm_";

    /// <summary>
    /// Exact query keys removed from links as tracking parameters. Compared case-insensitively.
    /// </summary>
    public static readonly string[] LinkTrackingParameters =
    [
        "fbclid", "gclid", "dclid", "gbraid", "wbraid", "msclkid", "mc_cid", "mc_eid", "mkt_tok",
        "_hsenc", "_hsmi", "vero_id", "yclid", "igshid", "ttclid", "twclid", "oly_enc_id", "oly_anon_id", "ss_email_id"
    ];

    /// <summary>
    /// Hosts that exist to record message opens. A host matches itself and its subdomains.
    /// Only services whose images are tracking pixels belong here: a content or CDN host would hide real pictures.
    /// </summary>
    public static readonly string[] TrackerHosts =
    [
        // Personal and sales open tracking
        "mailtrack.io",
        "t.yesware.com",
        "mailfoogae.appspot.com",
        "r.superhuman.com",
        "track.mixmax.com",
        "bl-1.com",
        "mailstat.us",
        "gml.email",
        "tracking.cirrusinsight.com",
        "t.hubspotemail.net",
        "t.hubspotfree.net",
        "tr.cloudmagic.com",
        "bowtie.mailbutler.io",
        "yamm-track.appspot.com",
        "emltrk.com",
        "eoapxl.com",
        "pixel.app.returnpath.net",

        // Bulk senders' open-tracking hosts
        "list-manage.com",
        "maillist-manage.com",
        "mandrillapp.com",
        "ct.sendgrid.net",
        "email.mailgun.net",
        "pstmrk.it",
        "rs6.net",
        "exct.net",
        "t.eloqua.com",
        "via.intercom.io",
        "track.customer.io",
        "trk.klaviyo.com",
        "open.convertkit-mail.com",
        "mjt.lu",
        "sendibt2.com",
        "sendibt3.com",
        "sendibtd.com",
        "awstrack.me",
        "spgo.io",
        "click.mailerlite.com",
        "stat-pulse.com"
    ];

    /// <summary>
    /// Image URL paths used only for open tracking. Kept narrow: a false positive hides a real picture.
    /// </summary>
    public const string TrackingPathPattern = @"^/(open|o|e/eo|wf/open|track/open|img/track)(/|\.(gif|png|php|aspx)$|$)";
}

