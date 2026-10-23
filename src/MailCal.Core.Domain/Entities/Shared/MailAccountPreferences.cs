using System;
using System.Collections.Generic;
using SQLite;
using MailCal.Core.Domain.Enums;

namespace MailCal.Core.Domain.Entities.Shared;

public partial class MailAccountPreferences
{
    [PrimaryKey]
    public Guid Id { get; set; }

    /// <summary>
    /// Id of the account in MailAccount table.
    /// </summary>
    public Guid AccountId { get; set; }

    /// <summary>
    /// Gets or sets whether sent draft messages should be appended to the sent folder.
    /// Some IMAP servers do this automatically, some don't.
    /// It's disabled by default.
    /// </summary>
    public bool ShouldAppendMessagesToSentFolder { get; set; }

    /// <summary>
    /// Gets or sets whether the notifications are enabled for the account.
    /// </summary>
    public bool IsNotificationsEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether this account overrides the app-wide notification defaults.
    /// While false, every field below is ignored and the defaults apply. Stored as an explicit flag
    /// rather than nullable overrides so switching back to the defaults keeps the account's choices.
    /// </summary>
    public bool HasCustomNotificationSettings { get; set; }

    /// <summary>
    /// Gets or sets whether new mail in this account raises a notification.
    /// Only read when <see cref="HasCustomNotificationSettings"/> is true.
    /// </summary>
    public bool IsNewMailNotificationEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets which folders of this account raise a notification.
    /// Only read when <see cref="HasCustomNotificationSettings"/> is true.
    /// </summary>
    public MailNotificationScope NotificationScope { get; set; } = MailNotificationScope.InboxOnly;

    /// <summary>
    /// Gets or sets how much of a message is shown on this account's notifications.
    /// Only read when <see cref="HasCustomNotificationSettings"/> is true.
    /// </summary>
    public MailNotificationContent NotificationContent { get; set; } = MailNotificationContent.SenderSubjectPreview;

    /// <summary>
    /// Gets or sets the sound played for this account's notifications.
    /// Only read when <see cref="HasCustomNotificationSettings"/> is true.
    /// </summary>
    public NotificationSoundEvent AccountNotificationSoundEvent { get; set; } = NotificationSoundEvent.Mail;

    /// <summary>
    /// Gets or sets whether calendars in this account raise reminder notifications.
    /// Only read when <see cref="HasCustomNotificationSettings"/> is true.
    /// </summary>
    public bool AreCalendarRemindersEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether this account respects the app-wide quiet hours schedule.
    /// Read whether or not the account overrides the other defaults, because it is a decision about
    /// the account itself rather than about how its notifications look.
    /// </summary>
    public AccountQuietHoursStance QuietHoursStance { get; set; } = AccountQuietHoursStance.Follow;

    /// <summary>
    /// Gets or sets whether the account has Focused inbox support.
    /// Null if the account provider type doesn't support Focused inbox.
    /// </summary>
    public bool? IsFocusedInboxEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether signature should be appended automatically.
    /// </summary>
    public bool IsSignatureEnabled { get; set; }

    /// <summary>
    /// Gets or sets whether this account's unread items should be included in taskbar badge.
    /// </summary>
    public bool IsTaskbarBadgeEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the account row in the navigation shows its unread total.
    /// </summary>
    public bool IsAccountBadgeEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether individual folder rows in the navigation show their own unread counts.
    /// This is visibility only. It never changes the account or taskbar totals.
    /// </summary>
    public bool AreFolderBadgesEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets which folders feed the account unread total.
    /// </summary>
    public UnreadBadgeCountSource UnreadBadgeCountSource { get; set; } = UnreadBadgeCountSource.InboxOnly;

    /// <summary>
    /// Gets or sets whether this account's enabled folders should be included in the taskbar jump list.
    /// </summary>
    public bool IsJumpListEnabled { get; set; } = true;

    // Columns of the removed cloud intelligence feature. They stay mapped so databases created by
    // Mail-Cal keep their schema; nothing reads them.
    public bool IsSemanticIndexingEnabled { get; set; }
    public bool IsDailyBriefingEnabled { get; set; }
    public string ExcludedIntelligenceIndicatorIdsStorage { get; set; } = string.Empty;
    public bool IsIntelligenceFolderSelectionInitialized { get; set; }
    public string SelectedIntelligenceFolderIdsStorage { get; set; } = string.Empty;
    public bool IsIntelligenceCoverageInitialized { get; set; }
    public string IntelligenceFolderCoverageStorage { get; set; } = string.Empty;
    public string IntelligenceDefaultCoverageStorage { get; set; } = string.Empty;
    public bool AutomaticallyIndexNewMessages { get; set; }
    public string SemanticIndexRangePresetId { get; set; }
    public DateTime? SemanticIndexRangeCutoffUtc { get; set; }
    public DateTime? SemanticIndexRangeThroughUtc { get; set; }

    /// <summary>
    /// Gets or sets signature for new messages. Null if signature is not needed.
    /// </summary>
    public Guid? SignatureIdForNewMessages { get; set; }

    /// <summary>
    /// Gets or sets signature for following messages. Null if signature is not needed.
    /// </summary>
    public Guid? SignatureIdForFollowingMessages { get; set; }
}
