using System;
using System.Diagnostics;
using Itenso.TimePeriod;
using SQLite;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Helpers;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.Entities.Calendar;

[DebuggerDisplay("{Title} ({StartDate} - {EndDate})")]
public class CalendarItem : ICalendarItem
{
    [PrimaryKey]
    public Guid Id { get; set; } = Guid.NewGuid();

    [NotNull]
    public string RemoteEventId { get; set; } = string.Empty;

    [NotNull]
    public Guid CalendarId { get; set; }

    [Ignore]
    public IAccountCalendar AssignedCalendar { get; set; }

    [NotNull]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Location { get; set; }
    public string? HtmlLink { get; set; }

    public DateTime StartDateTime { get; set; }

    public DateTime EndDateTime { get; set; }

    private ITimePeriod _period;
    public ITimePeriod Period
    {
        get
        {
            _period ??= new TimeRange(StartDateTime, EndDateTime);

            return _period;
        }
    }

    public bool IsAllDay { get; set; }

    public string? TimeZone { get; set; }

    public string? RecurrenceRules { get; set; }

    public string? Status { get; set; }

    public string? OrganizerDisplayName { get; set; }

    public string? OrganizerEmail { get; set; }

    public DateTime CreatedDate { get; set; }

    public DateTime LastModified { get; set; }

    public bool IsDeleted { get; set; }

    public string? RecurringEventId { get; set; }

    public string? OriginalStartTime { get; set; }

    /// <summary>
    /// The type of calendar item (Timed, AllDay, MultiDay, etc.)
    /// </summary>
    public CalendarItemType ItemType { get; set; }

    /// <summary>
    /// Automatically determines and sets the ItemType based on event properties
    /// </summary>
    public void DetermineItemType()
    {
        var hasRecurrence = !string.IsNullOrEmpty(RecurrenceRules);
        var isCancelled = Status?.ToLowerInvariant() == "cancelled" || IsDeleted;

        ItemType = CalendarItemTypeHelper.DetermineItemType(
            StartDateTime,
            EndDateTime,
            IsAllDay,
            hasRecurrence,
            isCancelled,
            Status);
    }
}
