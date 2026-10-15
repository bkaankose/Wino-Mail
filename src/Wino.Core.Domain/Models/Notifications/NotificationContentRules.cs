#nullable enable
using System;
using System.Linq;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;

namespace Wino.Core.Domain.Models.Notifications;

/// <summary>The text of one mail notification after the account's privacy level was applied.</summary>
/// <param name="Title">Sender, or the generic "new mail" title when the privacy level hides everything.</param>
/// <param name="Subtitle">Subject, when the privacy level shows it.</param>
/// <param name="Body">Preview text, when the privacy level shows it.</param>
public sealed record MailNotificationText(string Title, string? Subtitle, string? Body);

/// <summary>
/// What a notification says and when it is shown (the Windows NotificationBuilder rules), shared by every
/// platform builder so the Apple and Android notifications cannot drift: the notification scope, the privacy
/// level text, the summary threshold, the configured mail actions and the calendar reminder text.
/// </summary>
public static class NotificationContentRules
{
    /// <summary>More new mails than this in one batch show one "N new messages" summary instead.</summary>
    public const int MaxIndividualMailNotifications = 3;

    /// <summary>Windows IsWithinNotificationScope.</summary>
    public static bool IsWithinNotificationScope(MailCopy mailItem, MailNotificationScope scope)
    {
        if (scope == MailNotificationScope.AllFolders) return true;
        var isInbox = mailItem.AssignedFolder?.SpecialFolderType == SpecialFolderType.Inbox;
        return scope switch
        {
            MailNotificationScope.InboxOnly => isInbox,
            MailNotificationScope.FocusedInboxOnly => isInbox && mailItem.IsFocused,
            MailNotificationScope.InboxAndCustomFolders => isInbox || mailItem.AssignedFolder?.SpecialFolderType == SpecialFolderType.Other,
            _ => true
        };
    }

    /// <summary>Windows AddMailNotificationText: as much as the account's privacy level allows.</summary>
    public static MailNotificationText ComposeMailText(MailCopy mailItem, MailNotificationContent content)
    {
        if (content == MailNotificationContent.Nothing)
            return new MailNotificationText(Translator.Notifications_MultipleNotificationsTitle, null, null);

        var title = string.IsNullOrWhiteSpace(mailItem.FromName) ? mailItem.FromAddress ?? string.Empty : mailItem.FromName;
        if (content == MailNotificationContent.SenderOnly)
            return new MailNotificationText(title, null, null);

        return new MailNotificationText(title, mailItem.Subject,
            content == MailNotificationContent.SenderSubjectPreview ? mailItem.PreviewText : null);
    }

    /// <summary>Whether the privacy level allows the sender's picture.</summary>
    public static bool ShowsSenderPicture(MailNotificationContent content) => content != MailNotificationContent.Nothing;

    /// <summary>The summary shown instead of individual notifications for a large batch.</summary>
    public static MailNotificationText ComposeSummaryText(int count)
        => new(Translator.Notifications_MultipleNotificationsTitle, null, string.Format(Translator.Notifications_MultipleNotificationsMessage, count));

    /// <summary>Windows NotificationBuilder.GetConfiguredMailNotificationActions.</summary>
    public static (MailOperation First, MailOperation Second) ResolveMailActions(MailOperation configuredFirst, MailOperation configuredSecond)
    {
        var supported = NotificationActionIdentifiers.SupportedMailActions;
        var first = supported.Contains(configuredFirst) ? configuredFirst : MailOperation.MarkAsRead;
        var second = supported.Contains(configuredSecond) ? configuredSecond : MailOperation.SoftDelete;
        if (second == first) second = supported.First(action => action != first);
        return (first, second);
    }

    public static string MailActionTitle(MailOperation operation) => operation switch
    {
        MailOperation.MarkAsRead => Translator.MailOperation_MarkAsRead,
        MailOperation.SoftDelete => Translator.MailOperation_Delete,
        MailOperation.MoveToJunk => Translator.MailOperation_MarkAsJunk,
        MailOperation.Archive => Translator.MailOperation_Archive,
        MailOperation.Reply => Translator.MailOperation_Reply,
        MailOperation.ReplyAll => Translator.MailOperation_ReplyAll,
        MailOperation.Forward => Translator.MailOperation_Forward,
        _ => operation.ToString()
    };

    public static string SnoozeTitle(int minutes)
        => $"{Translator.CalendarReminder_SnoozeAction} ({string.Format(Translator.CalendarReminder_SnoozeMinutesOption, minutes)})";

    /// <summary>The reminder body: "Starts in 15 minutes - start", then the location on its own line.</summary>
    public static string ComposeCalendarReminderBody(CalendarItem calendarItem, DateTime nowLocal)
    {
        var localStart = calendarItem.GetLocalStartDate();
        var body = $"{GetCalendarReminderContext(localStart, nowLocal)} - {localStart:g}";
        if (!string.IsNullOrWhiteSpace(calendarItem.Location)) body += "\n" + calendarItem.Location;
        return body;
    }

    /// <summary>Windows GetCalendarReminderContext.</summary>
    public static string GetCalendarReminderContext(DateTime localStart, DateTime nowLocal)
    {
        var delta = localStart - nowLocal;
        var absDelta = delta.Duration();
        if (absDelta < TimeSpan.FromMinutes(1))
            return delta.TotalSeconds >= 0 ? Translator.CalendarReminder_StartingNow : Translator.CalendarReminder_StartedNow;
        if (delta.TotalSeconds > 0)
        {
            if (delta.TotalHours >= 1) return string.Format(Translator.CalendarReminder_StartsInHours, Math.Max(1, (int)Math.Floor(delta.TotalHours)));
            return string.Format(Translator.CalendarReminder_StartsInMinutes, Math.Max(1, (int)Math.Floor(delta.TotalMinutes)));
        }
        if (absDelta.TotalHours >= 1) return string.Format(Translator.CalendarReminder_StartedHoursAgo, Math.Max(1, (int)Math.Floor(absDelta.TotalHours)));
        return string.Format(Translator.CalendarReminder_StartedMinutesAgo, Math.Max(1, (int)Math.Floor(absDelta.TotalMinutes)));
    }

    /// <summary>
    /// The one snooze length a reminder offers (the "Default snooze duration" when the reminder allows a snooze,
    /// Windows' allowed-snooze rule), limited to the supported lengths; null when it offers none.
    /// </summary>
    public static int? ResolveReminderSnoozeMinutes(long reminderDurationInSeconds, long defaultReminderDurationInSeconds, int defaultSnoozeDurationInMinutes)
    {
        var minutes = CalendarReminderSnoozeOptions.GetDefaultSnoozeMinutes(reminderDurationInSeconds, defaultReminderDurationInSeconds, defaultSnoozeDurationInMinutes);
        return minutes is { } value && CalendarReminderSnoozeOptions.GetSupportedSnoozeMinutes().Contains(value) ? value : null;
    }
}
