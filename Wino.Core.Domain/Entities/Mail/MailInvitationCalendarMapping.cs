using System;
using SQLite;

namespace Wino.Core.Domain.Entities.Mail;

/// <summary>
/// Maps a calendar invitation mail item to a persisted calendar event.
/// </summary>
public class MailInvitationCalendarMapping
{
    [PrimaryKey]
    public Guid Id { get; set; }

    public Guid AccountId { get; set; }

    /// <summary>
    /// MailCopy.Id value of the invitation mail.
    /// </summary>
    public string MailCopyId { get; set; }

    /// <summary>
    /// iCalendar UID extracted from invitation MIME/ICS content.
    /// </summary>
    public string InvitationUid { get; set; }

    public Guid CalendarId { get; set; }
    public Guid CalendarItemId { get; set; }
    public string CalendarRemoteEventId { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
