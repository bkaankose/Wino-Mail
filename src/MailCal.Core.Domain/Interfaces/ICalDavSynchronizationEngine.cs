#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Calendar;
using MailCal.Core.Domain.Models.Synchronization;

namespace MailCal.Core.Domain.Interfaces;

/// <summary>
/// Synchronizes the CalDAV calendars of an IMAP account and writes user changes to the server.
/// The server is the source of truth: local rows follow what the server reports, and a change
/// is stored locally with the ETag the server returned for it.
/// </summary>
public interface ICalDavSynchronizationEngine
{
    /// <param name="reportProgress">Receives (total calendars, remaining calendars).</param>
    Task<CalendarSynchronizationResult> SynchronizeAsync(
        MailAccount account,
        CalendarSynchronizationOptions options,
        Action<int, int>? reportProgress = null,
        CancellationToken cancellationToken = default);

    /// <param name="attachments">
    /// Files to attach. They are uploaded as RFC 8607 managed attachments after the event is
    /// written when the server advertises <c>calendar-managed-attachments</c>; otherwise they are
    /// ignored and logged.
    /// </param>
    Task CreateEventAsync(
        MailAccount account,
        CalendarItem item,
        List<CalendarEventAttendee>? attendees,
        List<Reminder>? reminders,
        IReadOnlyList<CalendarEventComposeAttachmentDraft>? attachments = null,
        CancellationToken cancellationToken = default);

    /// <param name="attendees">Null loads the stored attendees of the item.</param>
    /// <param name="attachments">
    /// Null leaves the attachments unchanged. Otherwise the complete wanted list: a draft whose
    /// <see cref="CalendarEventComposeAttachmentDraft.Id"/> equals a stored
    /// <see cref="CalendarAttachment.Id"/> keeps that attachment, a draft with a readable
    /// <see cref="CalendarEventComposeAttachmentDraft.FilePath"/> is uploaded, and a stored managed
    /// attachment missing from the list is removed.
    /// </param>
    Task UpdateEventAsync(
        MailAccount account,
        CalendarItem item,
        List<CalendarEventAttendee>? attendees,
        IReadOnlyList<CalendarEventComposeAttachmentDraft>? attachments = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads one attachment of a CalDAV event to <paramref name="localFilePath"/> and marks it
    /// downloaded. The URL comes from the stored ICS copy; account credentials are only sent to
    /// the CalDAV server's own origins.
    /// </summary>
    Task DownloadAttachmentAsync(
        MailAccount account,
        CalendarItem item,
        CalendarAttachment attachment,
        string localFilePath,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a resource, or removes one occurrence from its series.</summary>
    Task DeleteEventAsync(MailAccount account, CalendarItem item, CancellationToken cancellationToken = default);

    /// <summary>Writes the user's answer to an invitation.</summary>
    Task RespondToEventAsync(
        MailAccount account,
        CalendarItem item,
        CalendarItemStatus response,
        CancellationToken cancellationToken = default);
}
