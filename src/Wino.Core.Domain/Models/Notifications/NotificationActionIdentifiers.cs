#nullable enable
using System;
using System.Linq;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Models.Notifications;

/// <summary>
/// Action identifiers notification builders give their buttons and <see cref="NotificationResponse"/>
/// carries back. Platforms with registered categories (macOS, iOS) use these strings as their action identifiers.
/// </summary>
public static class NotificationActionIdentifiers
{
    /// <summary>Mail action identifiers are this prefix plus the <see cref="MailOperation"/> name.</summary>
    public const string MailActionPrefix = "wino.mail.";
    public const string Open = "wino.open";
    public const string Dismiss = "wino.dismiss";
    public const string CalendarJoin = "wino.calendar.joinonline";
    public const string CalendarSnooze = "wino.calendar.snooze";

    /// <summary>Snooze length used when neither the notification nor the "Default snooze duration" setting gives one.</summary>
    public const int FallbackSnoozeMinutes = 5;

    /// <summary>The operations a mail notification can offer (the Windows toast set).</summary>
    public static readonly MailOperation[] SupportedMailActions =
    [
        MailOperation.MarkAsRead,
        MailOperation.SoftDelete,
        MailOperation.MoveToJunk,
        MailOperation.Archive,
        MailOperation.Reply,
        MailOperation.ReplyAll,
        MailOperation.Forward
    ];

    public static string MailAction(MailOperation operation) => MailActionPrefix + operation;

    public static bool TryParseMailAction(string? identifier, out MailOperation operation)
    {
        operation = MailOperation.None;
        return identifier is not null &&
               identifier.StartsWith(MailActionPrefix, StringComparison.Ordinal) &&
               Enum.TryParse(identifier[MailActionPrefix.Length..], out operation) &&
               SupportedMailActions.Contains(operation);
    }
}
