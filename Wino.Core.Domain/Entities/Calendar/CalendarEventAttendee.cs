using System;
using SQLite;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Calendar;

// TODO: Connect to Contact store with Wino People.
public class CalendarEventAttendee
{
    [PrimaryKey]
    public Guid Id { get; set; } = Guid.NewGuid();

    [NotNull]
    public Guid EventId { get; set; }

    [NotNull]
    public string Email { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    public AttendeeResponseStatus ResponseStatus { get; set; } = AttendeeResponseStatus.NeedsAction;

    public bool IsOptional { get; set; } = false;

    public bool IsOrganizer { get; set; } = false;

    public bool IsSelf { get; set; } = false;

    public string? Comment { get; set; }

    public int? AdditionalGuests { get; set; }

    public DateTime CreatedDate { get; set; }

    public DateTime LastModified { get; set; }
}
