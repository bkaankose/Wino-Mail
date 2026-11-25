using System;
using System.Collections.Generic;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Extensions;

namespace Wino.Core.Domain.Models.Links;

/// <summary>
/// Stable keys for <see cref="ItemLink"/>. A mail is keyed by its Message-ID because the provider id changes on a
/// move and one Gmail message sits in several folders; an event by its provider id, because synchronization merges
/// and re-creates its rows; a task by its id.
/// </summary>
public static class ItemLinkKeys
{
    private const string MailCopyIdPrefix = "id:";

    public static string ForMail(MailCopy mail)
    {
        ArgumentNullException.ThrowIfNull(mail);

        var messageId = MailHeaderExtensions.StripAngleBrackets(mail.MessageId)?.Trim();
        return string.IsNullOrWhiteSpace(messageId) ? MailCopyIdPrefix + mail.Id : messageId;
    }

    public static bool TryGetMailCopyId(string key, out string mailCopyId)
    {
        mailCopyId = string.Empty;
        if (string.IsNullOrEmpty(key) || !key.StartsWith(MailCopyIdPrefix, StringComparison.Ordinal))
            return false;

        mailCopyId = key[MailCopyIdPrefix.Length..];
        return mailCopyId.Length > 0;
    }

    public static string ForEvent(CalendarItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var providerId = item.RemoteEventId.GetProviderRemoteEventId();
        return string.IsNullOrWhiteSpace(providerId) || providerId.IsClientTrackingPlaceholder()
            ? item.Id.ToString("D")
            : providerId;
    }

    /// <summary>Every key an event may have been linked under: before and after the provider assigned its id.</summary>
    public static List<string> EventCandidates(CalendarItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var keys = new List<string> { ForEvent(item), item.Id.ToString("D") };
        if (item.RecurringCalendarItemId is Guid seriesId)
            keys.Add(seriesId.ToString("D"));
        if (item.RemoteEventId.GetClientTrackingId() is Guid trackedId)
            keys.Add(trackedId.ToString("D"));

        return keys;
    }

    public static string ForTask(AccountTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return ForTask(task.Id);
    }

    public static string ForTask(Guid taskId) => taskId.ToString("D");
}
