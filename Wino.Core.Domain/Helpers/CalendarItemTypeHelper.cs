using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Domain.Helpers;
/// <summary>
/// Helper class for CalendarItemType operations
/// </summary>
public static class CalendarItemTypeHelper
{
    /// <summary>
    /// Determines the calendar item type based on event properties
    /// </summary>
    /// <param name="startDateTime">Event start date/time</param>
    /// <param name="endDateTime">Event end date/time</param>
    /// <param name="isAllDay">Whether the event is marked as all-day</param>
    /// <param name="isRecurring">Whether the event has recurrence rules</param>
    /// <param name="isCancelled">Whether the event is cancelled</param>
    /// <param name="status">Event status</param>
    /// <returns>The appropriate CalendarItemType</returns>
    public static CalendarItemType DetermineItemType(
        DateTime startDateTime,
        DateTime endDateTime,
        bool isAllDay,
        bool isRecurring = false,
        bool isCancelled = false,
        string? status = null)
    {
        // Handle recurring events
        if (isRecurring)
        {
            return isAllDay ? CalendarItemType.RecurringAllDay : CalendarItemType.Recurring;
        }

        // Handle all-day events
        if (isAllDay)
        {
            var daySpan = (endDateTime.Date - startDateTime.Date).Days;
            return daySpan > 1 ? CalendarItemType.MultiDayAllDay : CalendarItemType.AllDay;
        }

        // Handle timed events
        var duration = endDateTime - startDateTime;



        // Multi-day timed events
        if (duration.TotalDays >= 1)
        {
            return CalendarItemType.MultiDay;
        }

        // Cross midnight events (same calendar day but extends past midnight)
        if (startDateTime.Date != endDateTime.Date && duration.TotalHours <= 24)
        {
            return CalendarItemType.CrossMidnight;
        }

        // Standard timed events
        return CalendarItemType.Timed;
    }

    /// <summary>
    /// Gets a human-readable description of the calendar item type
    /// </summary>
    /// <param name="itemType">The calendar item type</param>
    /// <returns>Description string</returns>
    public static string GetDescription(CalendarItemType itemType)
    {
        return itemType switch
        {
            CalendarItemType.Timed => "Timed Event",
            CalendarItemType.AllDay => "All-Day Event",
            CalendarItemType.MultiDay => "Multi-Day Event",
            CalendarItemType.MultiDayAllDay => "Multi-Day All-Day Event",
            CalendarItemType.Recurring => "Recurring Event",
            CalendarItemType.RecurringAllDay => "Recurring All-Day Event",
            CalendarItemType.RecurringException => "Modified Recurring Event",
            CalendarItemType.CrossMidnight => "Cross-Midnight Event",
            _ => "Unknown Event Type"
        };
    }

    /// <summary>
    /// Checks if the event type represents an all-day event
    /// </summary>
    /// <param name="itemType">The calendar item type</param>
    /// <returns>True if it's an all-day event type</returns>
    public static bool IsAllDayType(CalendarItemType itemType)
    {
        return itemType == CalendarItemType.AllDay ||
               itemType == CalendarItemType.MultiDayAllDay ||
               itemType == CalendarItemType.RecurringAllDay;
    }

    /// <summary>
    /// Checks if the event type represents a recurring event
    /// </summary>
    /// <param name="itemType">The calendar item type</param>
    /// <returns>True if it's a recurring event type</returns>
    public static bool IsRecurringType(CalendarItemType itemType)
    {
        return itemType == CalendarItemType.Recurring ||
               itemType == CalendarItemType.RecurringAllDay ||
               itemType == CalendarItemType.RecurringException;
    }

    /// <summary>
    /// Checks if the event type represents a multi-day event
    /// </summary>
    /// <param name="itemType">The calendar item type</param>
    /// <returns>True if it's a multi-day event type</returns>
    public static bool IsMultiDayType(CalendarItemType itemType)
    {
        return itemType == CalendarItemType.MultiDay ||
               itemType == CalendarItemType.MultiDayAllDay;
    }

    /// <summary>
    /// Gets the priority level for sorting events (lower number = higher priority)
    /// </summary>
    /// <param name="itemType">The calendar item type</param>
    /// <returns>Priority number for sorting</returns>
    public static int GetSortPriority(CalendarItemType itemType)
    {
        return itemType switch
        {

            CalendarItemType.AllDay => 2,
            CalendarItemType.MultiDayAllDay => 3,
            CalendarItemType.Timed => 4,
            CalendarItemType.CrossMidnight => 5,
            CalendarItemType.MultiDay => 6,
            CalendarItemType.Recurring => 7,
            CalendarItemType.RecurringAllDay => 8,
            CalendarItemType.RecurringException => 9,
            _ => 99
        };
    }
}
