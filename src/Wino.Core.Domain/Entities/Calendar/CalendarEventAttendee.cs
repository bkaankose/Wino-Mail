using System;
using SQLite;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Entities.Calendar;

public class CalendarEventAttendee
{
    [PrimaryKey]
    public Guid Id { get; set; }
    public Guid CalendarItemId { get; set; }
    public string Name { get; set; }
    public string Email { get; set; }
    public AttendeeStatus AttendenceStatus { get; set; }
    public bool IsOrganizer { get; set; }
    public bool IsOptionalAttendee { get; set; }
    public string Comment { get; set; }

    /// <summary>
    /// Resolved contact from the contact store. Populated at runtime via IContactService;
    /// not persisted to the database.
    /// </summary>
    [Ignore]
    public AccountContact ResolvedContact { get; set; }
}
