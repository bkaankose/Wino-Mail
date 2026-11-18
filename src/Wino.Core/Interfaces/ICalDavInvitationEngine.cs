#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Services;

namespace Wino.Core.Interfaces;

/// <summary>
/// The CalDAV side of answering a mail invitation (RFC 6047): who the user is on the calendar server, and the
/// attendee copy of the event. The calendar write and the local update are separate steps, so the caller can send
/// the reply in between and store nothing locally when sending fails.
/// </summary>
public interface ICalDavInvitationEngine
{
    Task<CalDavUserIdentity> GetUserIdentityAsync(MailAccount account, CancellationToken cancellationToken);

    /// <param name="createContent">Builds the object to write from the stored copy, or from nothing for a new event.</param>
    /// <returns>The server copy before the write (null for a new event) and the written copy.</returns>
    Task<(CalDavResourceSnapshot? Previous, CalDavResourceSnapshot Written)> WriteInvitationCopyAsync(
        MailAccount account,
        Guid calendarId,
        CalendarItem? existingItem,
        string uid,
        Func<string?, string> createContent,
        CancellationToken cancellationToken);

    Task RevertInvitationCopyAsync(
        MailAccount account,
        Guid calendarId,
        CalendarItem? existingItem,
        CalDavResourceSnapshot? previous,
        CalDavResourceSnapshot written,
        string uid,
        CancellationToken cancellationToken);

    Task ApplyInvitationCopyAsync(
        MailAccount account,
        Guid calendarId,
        CalendarItem? existingItem,
        CalDavResourceSnapshot written,
        CancellationToken cancellationToken);

    Task PersistInvitationResponseAsync(MailAccount account, CalendarItem item, CalDavUserIdentity identity, AttendeeStatus response);

    Task DeleteEventAsync(MailAccount account, CalendarItem item, CancellationToken cancellationToken = default);

    /// <summary>Sets the status of an imported event from the user's own attendee entry.</summary>
    void ApplyImportedResponseStatus(CalDavCalendarEvent remoteEvent, CalDavUserIdentity identity);
}
