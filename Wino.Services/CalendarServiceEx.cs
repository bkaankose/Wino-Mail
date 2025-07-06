using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services;

public class CalendarServiceEx : BaseDatabaseService, ICalendarServiceEx
{
    public CalendarServiceEx(IDatabaseService databaseService) : base(databaseService) { }

    public async Task<List<CalendarItem>> GetAllEventsAsync()
    {
        return await Connection.Table<CalendarItem>()
            .Where(e => !e.IsDeleted)
            .ToListAsync();
    }

    /// <summary>
    /// Gets all events from the database including soft-deleted ones
    /// </summary>
    /// <returns>List of all events including deleted ones</returns>
    public async Task<List<CalendarItem>> GetAllEventsIncludingDeletedAsync()
    {
        return await Connection.Table<CalendarItem>().ToListAsync();
    }

    public async Task<CalendarItem?> GetEventByRemoteIdAsync(string remoteEventId)
    {
        return await Connection.Table<CalendarItem>()
            .Where(e => e.RemoteEventId == remoteEventId && !e.IsDeleted)
            .FirstOrDefaultAsync();
    }

    public async Task<List<CalendarItem>> GetEventsInDateRangeAsync(DateTime startDate, DateTime endDate)
    {
        return await Connection.Table<CalendarItem>()
            .Where(e => e.StartDateTime >= startDate && e.StartDateTime <= endDate && !e.IsDeleted)
            .ToListAsync();
    }

    public async Task<List<CalendarItem>> GetRecurringEventsAsync()
    {
        return await Connection.Table<CalendarItem>()
            .Where(e => !string.IsNullOrEmpty(e.RecurrenceRules) && !e.IsDeleted)
            .ToListAsync();
    }

    public async Task<int> InsertEventAsync(CalendarItem calendarItem)
    {
        calendarItem.Id = Guid.NewGuid();
        calendarItem.CreatedDate = DateTime.UtcNow;
        calendarItem.LastModified = DateTime.UtcNow;
        return await Connection.InsertAsync(calendarItem);
    }

    public async Task<int> UpdateEventAsync(CalendarItem calendarItem)
    {
        calendarItem.LastModified = DateTime.UtcNow;
        return await Connection.UpdateAsync(calendarItem);
    }

    public async Task<int> UpsertEventAsync(CalendarItem calendarItem)
    {
        var existingEvent = await GetEventByRemoteIdAsync(calendarItem.RemoteEventId);
        if (existingEvent != null)
        {
            calendarItem.Id = existingEvent.Id;
            calendarItem.CreatedDate = existingEvent.CreatedDate;
            return await UpdateEventAsync(calendarItem);
        }
        else
        {
            return await InsertEventAsync(calendarItem);
        }
    }

    public async Task<int> DeleteEventAsync(string remoteEventId)
    {
        var existingEvent = await GetEventByRemoteIdAsync(remoteEventId);
        if (existingEvent != null)
        {
            existingEvent.IsDeleted = true;
            existingEvent.LastModified = DateTime.UtcNow;
            return await UpdateEventAsync(existingEvent);
        }
        return 0;
    }

    public async Task<int> HardDeleteEventAsync(string remoteEventId)
    {
        return await Connection.Table<CalendarItem>()
            .DeleteAsync(e => e.RemoteEventId == remoteEventId);
    }

    public async Task<List<CalendarItem>> GetEventsSinceLastSyncAsync(DateTime? lastSyncTime)
    {
        if (lastSyncTime == null)
        {
            return await GetAllEventsAsync();
        }

        return await Connection.Table<CalendarItem>()
            .Where(e => e.LastModified > lastSyncTime && !e.IsDeleted)
            .ToListAsync();
    }

    public async Task<int> ClearAllEventsAsync()
    {
        return await Connection.DeleteAllAsync<CalendarItem>();
    }

    // Calendar management methods
    public async Task<List<AccountCalendar>> GetAllCalendarsAsync()
    {
        return await Connection.Table<AccountCalendar>()
            .Where(c => !c.IsDeleted)
            .ToListAsync();
    }

    public async Task<AccountCalendar?> GetCalendarByRemoteIdAsync(string remoteCalendarId)
    {
        return await Connection.Table<AccountCalendar>()
            .Where(c => c.RemoteCalendarId == remoteCalendarId && !c.IsDeleted)
            .FirstOrDefaultAsync();
    }

    public async Task<int> InsertCalendarAsync(AccountCalendar calendar)
    {
        calendar.Id = Guid.NewGuid();
        calendar.CreatedDate = DateTime.UtcNow;
        calendar.LastModified = DateTime.UtcNow;
        return await Connection.InsertAsync(calendar);
    }

    public async Task<int> UpdateCalendarAsync(AccountCalendar calendar)
    {
        calendar.LastModified = DateTime.UtcNow;
        return await Connection.UpdateAsync(calendar);
    }

    public async Task<int> UpsertCalendarAsync(AccountCalendar calendar)
    {
        var existingCalendar = await GetCalendarByRemoteIdAsync(calendar.RemoteCalendarId);
        if (existingCalendar != null)
        {
            calendar.Id = existingCalendar.Id;
            calendar.CreatedDate = existingCalendar.CreatedDate;
            return await UpdateCalendarAsync(calendar);
        }
        else
        {
            return await InsertCalendarAsync(calendar);
        }
    }

    public async Task<int> DeleteCalendarAsync(string remoteCalendarId)
    {
        var existingCalendar = await GetCalendarByRemoteIdAsync(remoteCalendarId);
        if (existingCalendar != null)
        {
            existingCalendar.IsDeleted = true;
            existingCalendar.LastModified = DateTime.UtcNow;
            return await UpdateCalendarAsync(existingCalendar);
        }
        return 0;
    }

    /// <summary>
    /// Gets events for a specific calendar by internal Guid
    /// </summary>
    /// <param name="calendarId">The internal Guid of the calendar</param>
    /// <returns>List of events for the specified calendar</returns>
    public async Task<List<CalendarItem>> GetEventsForCalendarAsync(Guid calendarId)
    {
        return await Connection.Table<CalendarItem>()
            .Where(e => e.CalendarId == calendarId && !e.IsDeleted)
            .ToListAsync();
    }

    /// <summary>
    /// Gets events for a specific calendar by Remote Calendar ID
    /// </summary>
    /// <param name="remoteCalendarId">The Remote Calendar ID</param>
    /// <returns>List of events for the specified calendar</returns>
    public async Task<List<CalendarItem>> GetEventsByremoteCalendarIdAsync(string remoteCalendarId)
    {
        // First get the calendar to find its internal Guid
        var calendar = await GetCalendarByRemoteIdAsync(remoteCalendarId);
        if (calendar == null)
        {
            return new List<CalendarItem>();
        }

        return await GetEventsForCalendarAsync(calendar.Id);
    }

    public async Task<int> ClearAllCalendarsAsync()
    {
        return await Connection.DeleteAllAsync<AccountCalendar>();
    }

    public async Task<int> ClearAllDataAsync()
    {
        var calendarCount = await Connection.DeleteAllAsync<AccountCalendar>();
        var eventCount = await Connection.DeleteAllAsync<CalendarItem>();
        var calendareventattendeeCount = await Connection.DeleteAllAsync<CalendarEventAttendee>();
        return calendarCount + eventCount + calendareventattendeeCount;
    }

    /// <summary>
    /// Gets all events (including expanded recurring event instances) within a date range
    /// </summary>
    /// <param name="startDate">Start date of the range</param>
    /// <param name="endDate">End date of the range</param>
    /// <returns>List of events including expanded recurring instances</returns>
    public async Task<List<CalendarItem>> GetExpandedEventsInDateRangeAsync(DateTime startDate, DateTime endDate)
    {
        var allEvents = new List<CalendarItem>();

        // Get all non-recurring events in the date range
        var oneTimeEvents = await Connection.Table<CalendarItem>()
            .Where(e => !e.IsDeleted &&
                       (string.IsNullOrEmpty(e.RecurrenceRules) || e.RecurrenceRules == "") &&
                       e.StartDateTime >= startDate && e.StartDateTime <= endDate)
            .ToListAsync();

        allEvents.AddRange(oneTimeEvents);

        // Get all recurring events
        var recurringEvents = await Connection.Table<CalendarItem>()
            .Where(e => !e.IsDeleted &&
                       !string.IsNullOrEmpty(e.RecurrenceRules) &&
                       e.RecurrenceRules != "")
            .ToListAsync();

        // Expand recurring events
        foreach (var recurringEvent in recurringEvents)
        {
            var expandedInstances = ExpandRecurringEvent(recurringEvent, startDate, endDate);
            allEvents.AddRange(expandedInstances);
        }

        // Sort by start date and return
        return allEvents.OrderBy(e => e.StartDateTime).ToList();
    }

    /// <summary>
    /// Expands a recurring event into individual instances within the specified date range
    /// </summary>
    /// <param name="recurringEvent">The recurring event to expand</param>
    /// <param name="rangeStart">Start of the date range</param>
    /// <param name="rangeEnd">End of the date range</param>
    /// <returns>List of event instances</returns>
    private List<CalendarItem> ExpandRecurringEvent(CalendarItem recurringEvent, DateTime rangeStart, DateTime rangeEnd)
    {
        var instances = new List<CalendarItem>();

        if (string.IsNullOrEmpty(recurringEvent.RecurrenceRules))
            return instances;

        try
        {
            var recurrenceRules = recurringEvent.RecurrenceRules.Split(';');
            var rrule = recurrenceRules.FirstOrDefault(r => r.StartsWith("RRULE:"));

            if (string.IsNullOrEmpty(rrule))
                return instances;

            // Parse RRULE
            var ruleData = ParseRRule(rrule.Substring(6)); // Remove "RRULE:" prefix

            if (ruleData == null || !ruleData.ContainsKey("FREQ"))
                return instances;

            var frequency = ruleData["FREQ"];
            var interval = ruleData.ContainsKey("INTERVAL") ? int.Parse(ruleData["INTERVAL"]) : 1;
            var count = ruleData.ContainsKey("COUNT") ? int.Parse(ruleData["COUNT"]) : (int?)null;
            var until = ruleData.ContainsKey("UNTIL") ? ParseUntilDate(ruleData["UNTIL"]) : (DateTime?)null;

            // Calculate event duration
            var duration = recurringEvent.EndDateTime - recurringEvent.StartDateTime;

            // Start from the original event date
            var currentDate = recurringEvent.StartDateTime;
            var instanceCount = 0;
            var maxInstances = count ?? 1000; // Limit to prevent infinite loops

            // Generate instances
            while (instanceCount < maxInstances &&
                   currentDate <= rangeEnd &&
                   (until == null || currentDate <= until))
            {
                // Check if this instance falls within our range
                if (currentDate >= rangeStart && currentDate <= rangeEnd)
                {
                    var instance = CreateEventInstance(recurringEvent, currentDate, duration, instanceCount);
                    instances.Add(instance);
                }

                // Move to next occurrence based on frequency
                currentDate = GetNextOccurrence(currentDate, frequency, interval, ruleData);
                instanceCount++;

                // Safety check to prevent infinite loops
                if (instanceCount > 10000)
                    break;
            }
        }
        catch (Exception ex)
        {
            // Log error but don't crash - return empty list
            Console.WriteLine($"Error expanding recurring event {recurringEvent.RemoteEventId}: {ex.Message}");
        }

        return instances;
    }

    /// <summary>
    /// Parses an RRULE string into a dictionary of key-value pairs
    /// </summary>
    /// <param name="rrule">The RRULE string (without RRULE: prefix)</param>
    /// <returns>Dictionary of rule parameters</returns>
    private Dictionary<string, string>? ParseRRule(string rrule)
    {
        try
        {
            var ruleData = new Dictionary<string, string>();
            var parts = rrule.Split(';');

            foreach (var part in parts)
            {
                var keyValue = part.Split('=');
                if (keyValue.Length == 2)
                {
                    ruleData[keyValue[0].Trim()] = keyValue[1].Trim();
                }
            }

            return ruleData;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses UNTIL date from RRULE
    /// </summary>
    /// <param name="untilString">UNTIL date string</param>
    /// <returns>Parsed DateTime or null</returns>
    private DateTime? ParseUntilDate(string untilString)
    {
        try
        {
            // Handle different UNTIL formats
            if (untilString.Length == 8) // YYYYMMDD
            {
                return DateTime.ParseExact(untilString, "yyyyMMdd", null);
            }
            else if (untilString.Length == 15 && untilString.EndsWith("Z")) // YYYYMMDDTHHMMSSZ
            {
                return DateTime.ParseExact(untilString, "yyyyMMddTHHmmssZ", null);
            }
            else if (untilString.Length == 16) // YYYYMMDDTHHMMSSZ without Z
            {
                return DateTime.ParseExact(untilString.Substring(0, 15), "yyyyMMddTHHmmss", null);
            }
        }
        catch
        {
            // Return null if parsing fails
        }
        return null;
    }

    /// <summary>
    /// Creates an instance of a recurring event
    /// </summary>
    /// <param name="originalEvent">The original recurring event</param>
    /// <param name="instanceStart">Start time for this instance</param>
    /// <param name="duration">Duration of the event</param>
    /// <param name="instanceNumber">Instance number</param>
    /// <returns>Event instance</returns>
    private CalendarItem CreateEventInstance(CalendarItem originalEvent, DateTime instanceStart, TimeSpan duration, int instanceNumber)
    {
        return new CalendarItem
        {
            Id = Guid.NewGuid(),
            RemoteEventId = $"{originalEvent.RemoteEventId}_instance_{instanceNumber}",
            CalendarId = originalEvent.CalendarId,
            Title = originalEvent.Title,
            Description = originalEvent.Description,
            Location = originalEvent.Location,
            StartDateTime = instanceStart,
            EndDateTime = instanceStart + duration,
            IsAllDay = originalEvent.IsAllDay,
            TimeZone = originalEvent.TimeZone,
            RecurrenceRules = "", // Instances don't have recurrence rules
            Status = originalEvent.Status,
            OrganizerDisplayName = originalEvent.OrganizerDisplayName,
            OrganizerEmail = originalEvent.OrganizerEmail,
            CreatedDate = originalEvent.CreatedDate,
            LastModified = originalEvent.LastModified,
            IsDeleted = false,
            RecurringEventId = originalEvent.RemoteEventId,
            OriginalStartTime = instanceStart.ToString("O")
        };
    }

    /// <summary>
    /// Calculates the next occurrence based on frequency and interval
    /// </summary>
    /// <param name="currentDate">Current occurrence date</param>
    /// <param name="frequency">Frequency (DAILY, WEEKLY, MONTHLY, YEARLY)</param>
    /// <param name="interval">Interval between occurrences</param>
    /// <param name="ruleData">Additional rule data</param>
    /// <returns>Next occurrence date</returns>
    private DateTime GetNextOccurrence(DateTime currentDate, string frequency, int interval, Dictionary<string, string> ruleData)
    {
        switch (frequency.ToUpperInvariant())
        {
            case "DAILY":
                return currentDate.AddDays(interval);

            case "WEEKLY":
                // Handle BYDAY for weekly recurrence
                if (ruleData.ContainsKey("BYDAY"))
                {
                    var byDays = ruleData["BYDAY"].Split(',');
                    return GetNextWeeklyOccurrence(currentDate, interval, byDays);
                }
                return currentDate.AddDays(7 * interval);

            case "MONTHLY":
                // Handle BYMONTHDAY and BYDAY for monthly recurrence
                if (ruleData.ContainsKey("BYMONTHDAY"))
                {
                    var monthDay = int.Parse(ruleData["BYMONTHDAY"]);
                    return GetNextMonthlyByMonthDay(currentDate, interval, monthDay);
                }
                else if (ruleData.ContainsKey("BYDAY"))
                {
                    return GetNextMonthlyByDay(currentDate, interval, ruleData["BYDAY"]);
                }
                return currentDate.AddMonths(interval);

            case "YEARLY":
                return currentDate.AddYears(interval);

            default:
                return currentDate.AddDays(interval); // Default to daily
        }
    }

    /// <summary>
    /// Gets next weekly occurrence considering BYDAY rule
    /// </summary>
    private DateTime GetNextWeeklyOccurrence(DateTime currentDate, int interval, string[] byDays)
    {
        var dayMap = new Dictionary<string, DayOfWeek>
            {
                {"SU", DayOfWeek.Sunday}, {"MO", DayOfWeek.Monday}, {"TU", DayOfWeek.Tuesday},
                {"WE", DayOfWeek.Wednesday}, {"TH", DayOfWeek.Thursday}, {"FR", DayOfWeek.Friday}, {"SA", DayOfWeek.Saturday}
            };

        var targetDays = byDays.Where(d => dayMap.ContainsKey(d)).Select(d => dayMap[d]).OrderBy(d => d).ToList();

        if (!targetDays.Any())
            return currentDate.AddDays(7 * interval);

        var currentDayOfWeek = currentDate.DayOfWeek;
        var nextDay = targetDays.FirstOrDefault(d => d > currentDayOfWeek);

        if (nextDay != default(DayOfWeek))
        {
            // Next occurrence is later this week
            var daysToAdd = (int)nextDay - (int)currentDayOfWeek;
            return currentDate.AddDays(daysToAdd);
        }
        else
        {
            // Next occurrence is next week
            var daysToAdd = (7 * interval) - (int)currentDayOfWeek + (int)targetDays.First();
            return currentDate.AddDays(daysToAdd);
        }
    }

    /// <summary>
    /// Gets next monthly occurrence by month day
    /// </summary>
    private DateTime GetNextMonthlyByMonthDay(DateTime currentDate, int interval, int monthDay)
    {
        var nextMonth = currentDate.AddMonths(interval);
        var daysInMonth = DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month);
        var targetDay = Math.Min(monthDay, daysInMonth);

        return new DateTime(nextMonth.Year, nextMonth.Month, targetDay,
                           currentDate.Hour, currentDate.Minute, currentDate.Second);
    }

    /// <summary>
    /// Gets next monthly occurrence by day (e.g., first Monday, last Friday)
    /// </summary>
    private DateTime GetNextMonthlyByDay(DateTime currentDate, int interval, string byDay)
    {
        // This is a simplified implementation
        // Full implementation would handle patterns like "1MO" (first Monday), "-1FR" (last Friday)
        return currentDate.AddMonths(interval);
    }

    /// <summary>
    /// Gets all events (including expanded recurring event instances) within a date range with proper exception handling
    /// </summary>
    /// <param name="startDate">Start date of the range</param>
    /// <param name="endDate">End date of the range</param>
    /// <returns>List of events including expanded recurring instances, excluding canceled exceptions</returns>
    public async Task<List<CalendarItem>> GetExpandedEventsInDateRangeWithExceptionsAsync(DateTime startDate, DateTime endDate, AccountCalendar calendar)
    {
        var allEvents = new List<CalendarItem>();

        // Get all non-recurring events in the date range
        var oneTimeEvents = await Connection.Table<CalendarItem>()
            .Where(e => !e.IsDeleted &&
                       (string.IsNullOrEmpty(e.RecurrenceRules) || e.RecurrenceRules == "") &&
                       string.IsNullOrEmpty(e.RecurringEventId) && // Ensure it's not a modified instance
                       e.StartDateTime >= startDate && e.StartDateTime <= endDate
                       && e.CalendarId == calendar.Id)
            .ToListAsync();

        allEvents.AddRange(oneTimeEvents);

        // Get all recurring events (master events only)
        var recurringEvents = await Connection.Table<CalendarItem>()
            .Where(e => !e.IsDeleted &&
                       !string.IsNullOrEmpty(e.RecurrenceRules) &&
                       e.RecurrenceRules != "" &&
                       string.IsNullOrEmpty(e.RecurringEventId) &&
                       e.CalendarId == calendar.Id) // Master events, not instances
            .ToListAsync();

        // Get all exception instances (modified or moved instances)
        var exceptionInstances = await Connection.Table<CalendarItem>()
            .Where(e => !e.IsDeleted &&
             e.CalendarId == calendar.Id &&
                       !string.IsNullOrEmpty(e.RecurringEventId) &&
                       e.StartDateTime >= startDate && e.StartDateTime <= endDate)
            .ToListAsync();

        // Get all canceled instances (marked as deleted but with RecurringEventId)
        var canceledInstances = await Connection.Table<CalendarItem>()
            .Where(e => e.IsDeleted &&
            e.CalendarId == calendar.Id &&
                       !string.IsNullOrEmpty(e.RecurringEventId) &&
                       !string.IsNullOrEmpty(e.OriginalStartTime))
            .ToListAsync();

        // Group exceptions and cancellations by their parent recurring event
        var exceptionsByParent = exceptionInstances
            .Where(e => !string.IsNullOrEmpty(e.RecurringEventId))
            .GroupBy(e => e.RecurringEventId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        var canceledInstancesByParent = canceledInstances
            .Where(e => !string.IsNullOrEmpty(e.RecurringEventId))
            .GroupBy(e => e.RecurringEventId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Expand recurring events with exception handling
        foreach (var recurringEvent in recurringEvents)
        {
            var exceptions = exceptionsByParent.GetValueOrDefault(recurringEvent.RemoteEventId, new List<CalendarItem>());
            var canceled = canceledInstancesByParent.GetValueOrDefault(recurringEvent.RemoteEventId, new List<CalendarItem>());

            var expandedInstances = ExpandRecurringEventWithExceptions(recurringEvent, startDate, endDate, exceptions, canceled);
            allEvents.AddRange(expandedInstances);
        }

        // Add the exception instances (modified/moved instances) to the final list
        allEvents.AddRange(exceptionInstances);

        // Sort by start date and return
        return allEvents.OrderBy(e => e.StartDateTime).ToList();
    }

    /// <summary>
    /// Expands a recurring event into individual instances within the specified date range, with handling for exceptions and cancellations
    /// </summary>
    /// <param name="recurringEvent">The recurring event to expand</param>
    /// <param name="rangeStart">Start of the date range</param>
    /// <param name="rangeEnd">End of the date range</param>
    /// <param name="exceptions">List of modified instances for this recurring event</param>
    /// <param name="canceled">List of canceled instances for this recurring event</param>
    /// <returns>List of event instances excluding canceled ones</returns>
    private List<CalendarItem> ExpandRecurringEventWithExceptions(
        CalendarItem recurringEvent,
        DateTime rangeStart,
        DateTime rangeEnd,
        List<CalendarItem> exceptions,
        List<CalendarItem> canceled)
    {
        var instances = new List<CalendarItem>();

        if (string.IsNullOrEmpty(recurringEvent.RecurrenceRules))
            return instances;

        try
        {
            // Parse EXDATE (exception dates) from recurrence rules
            var exceptionDates = ParseExceptionDates(recurringEvent.RecurrenceRules);

            // Create sets of canceled and modified dates for quick lookup
            var canceledDates = new HashSet<DateTime>();
            var modifiedDates = new HashSet<DateTime>();

            // Add canceled instances to the set
            foreach (var canceledInstance in canceled)
            {
                if (!string.IsNullOrEmpty(canceledInstance.OriginalStartTime))
                {
                    if (DateTime.TryParse(canceledInstance.OriginalStartTime, out var originalDate))
                    {
                        canceledDates.Add(originalDate.Date);
                    }
                }
            }

            // Add modified instances to the set
            foreach (var exception in exceptions)
            {
                if (!string.IsNullOrEmpty(exception.OriginalStartTime))
                {
                    if (DateTime.TryParse(exception.OriginalStartTime, out var originalDate))
                    {
                        modifiedDates.Add(originalDate.Date);
                    }
                }
            }

            // Generate base instances using existing logic
            var baseInstances = ExpandRecurringEvent(recurringEvent, rangeStart, rangeEnd);

            // Filter out canceled, modified, and EXDATE instances
            foreach (var instance in baseInstances)
            {
                var instanceDate = instance.StartDateTime.Date;

                // Skip if this instance is canceled
                if (canceledDates.Contains(instanceDate))
                {
                    Console.WriteLine($"Skipping canceled instance: {instance.Title} on {instanceDate:MM/dd/yyyy}");
                    continue;
                }

                // Skip if this instance has been modified (the modified version will be added separately)
                if (modifiedDates.Contains(instanceDate))
                {
                    Console.WriteLine($"Skipping modified instance: {instance.Title} on {instanceDate:MM/dd/yyyy} (modified version exists)");
                    continue;
                }

                // Skip if this date is in EXDATE list
                if (exceptionDates.Contains(instanceDate))
                {
                    Console.WriteLine($"Skipping EXDATE instance: {instance.Title} on {instanceDate:MM/dd/yyyy}");
                    continue;
                }

                instances.Add(instance);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error expanding recurring event with exceptions {recurringEvent.RemoteEventId}: {ex.Message}");
            // Fall back to basic expansion without exception handling
            return ExpandRecurringEvent(recurringEvent, rangeStart, rangeEnd);
        }

        return instances;
    }

    /// <summary>
    /// Parses EXDATE (exception dates) from recurrence rules
    /// </summary>
    /// <param name="recurrenceRules">The full recurrence rules string</param>
    /// <returns>Set of exception dates</returns>
    private HashSet<DateTime> ParseExceptionDates(string recurrenceRules)
    {
        var exceptionDates = new HashSet<DateTime>();

        if (string.IsNullOrEmpty(recurrenceRules))
            return exceptionDates;

        try
        {
            var rules = recurrenceRules.Split(';');

            foreach (var rule in rules)
            {
                if (rule.StartsWith("EXDATE"))
                {
                    // Handle different EXDATE formats
                    // EXDATE:20250711T100000Z
                    // EXDATE;TZID=America/New_York:20250711T100000

                    var exdateValue = rule.Contains(':') ? rule.Split(':')[1] : rule;
                    var dates = exdateValue.Split(',');

                    foreach (var dateStr in dates)
                    {
                        var cleanDateStr = dateStr.Trim();
                        DateTime exceptionDate;

                        // Try different date formats
                        if (cleanDateStr.Length == 8) // YYYYMMDD
                        {
                            if (DateTime.TryParseExact(cleanDateStr, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out exceptionDate))
                            {
                                exceptionDates.Add(exceptionDate.Date);
                            }
                        }
                        else if (cleanDateStr.Length >= 15) // YYYYMMDDTHHMMSS or YYYYMMDDTHHMMSSZ
                        {
                            var dateOnly = cleanDateStr.Substring(0, 8);
                            if (DateTime.TryParseExact(dateOnly, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out exceptionDate))
                            {
                                exceptionDates.Add(exceptionDate.Date);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing EXDATE: {ex.Message}");
        }

        return exceptionDates;
    }

    /// <summary>
    /// Stores sync token for a calendar to enable delta synchronization
    /// </summary>
    /// <param name="calendarId">The calendar ID</param>
    /// <param name="syncToken">The sync token from Remote Calendar API</param>
    public async Task<int> UpdateCalendarSyncTokenAsync(string calendarId, string syncToken)
    {
        var calendar = await GetCalendarByRemoteIdAsync(calendarId);
        if (calendar != null)
        {
            calendar.SynchronizationDeltaToken = syncToken;
            calendar.LastSyncTime = DateTime.UtcNow;
            return await UpdateCalendarAsync(calendar);
        }
        return 0;
    }

    /// <summary>
    /// Gets the sync token for a calendar
    /// </summary>
    /// <param name="calendarId">The calendar ID</param>
    /// <returns>The sync token or null if not found</returns>
    public async Task<string?> GetCalendarSyncTokenAsync(string calendarId)
    {
        var calendar = await GetCalendarByRemoteIdAsync(calendarId);
        return calendar?.SynchronizationDeltaToken;
    }

    /// <summary>
    /// Marks an event as deleted (soft delete) for delta sync
    /// </summary>
    /// <param name="remoteEventId">The Remote event ID</param>
    /// <param name="remoteCalendarId">The Remote calendar ID</param>
    public async Task<int> MarkEventAsDeletedAsync(string remoteEventId, string remoteCalendarId)
    {
        var existingEvent = await GetEventByRemoteIdAsync(remoteEventId);
        if (existingEvent != null)
        {
            existingEvent.IsDeleted = true;
            existingEvent.Status = "cancelled";
            existingEvent.LastModified = DateTime.UtcNow;
            return await UpdateEventAsync(existingEvent);
        }

        // If event doesn't exist locally, create a placeholder for the cancellation
        // First get the calendar to find its internal Guid
        var calendar = await GetCalendarByRemoteIdAsync(remoteCalendarId);
        if (calendar == null)
        {
            throw new InvalidOperationException($"Calendar not found for Remote Calendar ID: {remoteCalendarId}");
        }

        var canceledEvent = new CalendarItem
        {
            Id = Guid.NewGuid(),
            RemoteEventId = remoteEventId,
            CalendarId = calendar.Id,
            Title = "[Canceled Event]",
            Status = "cancelled",
            IsDeleted = true,
            CreatedDate = DateTime.UtcNow,
            LastModified = DateTime.UtcNow,
            StartDateTime = DateTime.MinValue,
            EndDateTime = DateTime.MinValue
        };

        return await Connection.InsertAsync(canceledEvent);
    }

    /// <summary>
    /// Gets the last synchronization time for a calendar
    /// </summary>
    /// <param name="calendarId">The calendar ID</param>
    /// <returns>Last sync time or null if never synced</returns>
    public async Task<DateTime?> GetLastSyncTimeAsync(string calendarId)
    {
        var calendar = await GetCalendarByRemoteIdAsync(calendarId);
        return calendar?.LastSyncTime;
    }

    // CalendarEventAttendee management methods

    /// <summary>
    /// Gets all calendareventattendees for a specific event
    /// </summary>
    /// <param name="eventId">The internal event Guid</param>
    /// <returns>List of calendareventattendees for the event</returns>
    public async Task<List<CalendarEventAttendee>> GetCalendarEventAttendeesForEventAsync(Guid eventId)
    {
        return await Connection.Table<CalendarEventAttendee>()
            .Where(a => a.EventId == eventId)
            .OrderBy(a => a.DisplayName ?? a.Email)
            .ToListAsync();
    }

    /// <summary>
    /// Gets all calendareventattendees for a specific event by Remote Event ID
    /// </summary>
    /// <param name="remoteEventId">The Remote Event ID</param>
    /// <returns>List of calendareventattendees for the event</returns>
    public async Task<List<CalendarEventAttendee>> GetCalendarEventAttendeesForEventByRemoteIdAsync(string remoteEventId)
    {
        var calendarItem = await GetEventByRemoteIdAsync(remoteEventId);
        if (calendarItem == null)
        {
            return new List<CalendarEventAttendee>();
        }

        return await GetCalendarEventAttendeesForEventAsync(calendarItem.Id);
    }

    /// <summary>
    /// Inserts a new calendareventattendee
    /// </summary>
    /// <param name="calendareventattendee">The calendareventattendee to insert</param>
    /// <returns>Number of rows affected</returns>
    public async Task<int> InsertCalendarEventAttendeeAsync(CalendarEventAttendee calendareventattendee)
    {
        calendareventattendee.Id = Guid.NewGuid();
        calendareventattendee.CreatedDate = DateTime.UtcNow;
        calendareventattendee.LastModified = DateTime.UtcNow;
        return await Connection.InsertAsync(calendareventattendee);
    }

    /// <summary>
    /// Updates an existing calendareventattendee
    /// </summary>
    /// <param name="calendareventattendee">The calendareventattendee to update</param>
    /// <returns>Number of rows affected</returns>
    public async Task<int> UpdateCalendarEventAttendeeAsync(CalendarEventAttendee calendareventattendee)
    {
        calendareventattendee.LastModified = DateTime.UtcNow;
        return await Connection.UpdateAsync(calendareventattendee);
    }

    /// <summary>
    /// Syncs calendareventattendees for an event (replaces all existing calendareventattendees)
    /// </summary>
    /// <param name="eventId">The internal event Guid</param>
    /// <param name="calendareventattendees">List of calendareventattendees to sync</param>
    /// <returns>Number of calendareventattendees synced</returns>
    public async Task<int> SyncCalendarEventAttendeesForEventAsync(Guid eventId, List<CalendarEventAttendee> calendareventattendees)
    {
        // Delete existing calendareventattendees for this event
        await Connection.Table<CalendarEventAttendee>()
            .Where(a => a.EventId == eventId)
            .DeleteAsync();

        // Insert new calendareventattendees
        int syncedCount = 0;
        foreach (var calendareventattendee in calendareventattendees)
        {
            calendareventattendee.EventId = eventId;
            await InsertCalendarEventAttendeeAsync(calendareventattendee);
            syncedCount++;
        }

        return syncedCount;
    }

    /// <summary>
    /// Deletes all calendareventattendees for a specific event
    /// </summary>
    /// <param name="eventId">The internal event Guid</param>
    /// <returns>Number of calendareventattendees deleted</returns>
    public async Task<int> DeleteCalendarEventAttendeesForEventAsync(Guid eventId)
    {
        return await Connection.Table<CalendarEventAttendee>()
            .Where(a => a.EventId == eventId)
            .DeleteAsync();
    }

    /// <summary>
    /// Gets calendareventattendee count by response status
    /// </summary>
    /// <param name="eventId">The internal event Guid</param>
    /// <returns>Dictionary with response status counts</returns>
    public async Task<Dictionary<AttendeeResponseStatus, int>> GetCalendarEventAttendeeResponseCountsAsync(Guid eventId)
    {
        var calendareventattendees = await GetCalendarEventAttendeesForEventAsync(eventId);
        return calendareventattendees
            .GroupBy(a => a.ResponseStatus)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// Clears all calendareventattendees from the database
    /// </summary>
    /// <returns>Number of calendareventattendees deleted</returns>
    public async Task<int> ClearAllCalendarEventAttendeesAsync()
    {
        return await Connection.DeleteAllAsync<CalendarEventAttendee>();
    }

    /// <summary>
    /// Gets all calendareventattendees from the database
    /// </summary>
    /// <returns>List of all calendareventattendees</returns>
    public async Task<List<CalendarEventAttendee>> GetAllCalendarEventAttendeesAsync()
    {
        return await Connection.Table<CalendarEventAttendee>().ToListAsync();
    }

    /// <summary>
    /// Gets events by calendar item type
    /// </summary>
    /// <param name="itemType">The calendar item type to filter by</param>
    /// <returns>List of events matching the item type</returns>
    public async Task<List<CalendarItem>> GetEventsByItemTypeAsync(CalendarItemType itemType)
    {
        return await Connection.Table<CalendarItem>()
            .Where(e => !e.IsDeleted && e.ItemType == itemType)
            .OrderBy(e => e.StartDateTime)
            .ToListAsync();
    }

    /// <summary>
    /// Gets events by multiple calendar item types
    /// </summary>
    /// <param name="itemTypes">The calendar item types to filter by</param>
    /// <returns>List of events matching any of the item types</returns>
    public async Task<List<CalendarItem>> GetEventsByItemTypesAsync(params CalendarItemType[] itemTypes)
    {
        var events = await Connection.Table<CalendarItem>()
            .Where(e => !e.IsDeleted)
            .ToListAsync();

        return events
            .Where(e => itemTypes.Contains(e.ItemType))
            .OrderBy(e => e.StartDateTime)
            .ToList();
    }

    /// <summary>
    /// Gets all-day events (all types of all-day events)
    /// </summary>
    /// <returns>List of all-day events</returns>
    public async Task<List<CalendarItem>> GetAllDayEventsAsync()
    {
        return await GetEventsByItemTypesAsync(
            CalendarItemType.AllDay,
            CalendarItemType.MultiDayAllDay,
            CalendarItemType.RecurringAllDay);
    }

    /// <summary>
    /// Gets all recurring events by item type (all types of recurring events)
    /// </summary>
    /// <returns>List of recurring events</returns>
    public async Task<List<CalendarItem>> GetAllRecurringEventsByTypeAsync()
    {
        return await GetEventsByItemTypesAsync(
            CalendarItemType.Recurring,
            CalendarItemType.RecurringAllDay,
            CalendarItemType.RecurringException);
    }

    /// <summary>
    /// Gets multi-day events (all types of multi-day events)
    /// </summary>
    /// <returns>List of multi-day events</returns>
    public async Task<List<CalendarItem>> GetMultiDayEventsAsync()
    {
        return await GetEventsByItemTypesAsync(
            CalendarItemType.MultiDay,
            CalendarItemType.MultiDayAllDay);
    }

    /// <summary>
    /// Gets event statistics grouped by item type
    /// </summary>
    /// <returns>Dictionary with item type counts</returns>
    public async Task<Dictionary<CalendarItemType, int>> GetEventStatsByItemTypeAsync()
    {
        var allEvents = await Connection.Table<CalendarItem>()
            .Where(e => !e.IsDeleted)
            .ToListAsync();

        return allEvents
            .GroupBy(e => e.ItemType)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// Updates all existing events to determine their item types
    /// This is useful for migrating existing data to use the new ItemType property
    /// </summary>
    /// <returns>Number of events updated</returns>
    public async Task<int> UpdateAllEventItemTypesAsync()
    {
        var allEvents = await Connection.Table<CalendarItem>()
            .ToListAsync();

        int updatedCount = 0;
        foreach (var calendarItem in allEvents)
        {
            var oldItemType = calendarItem.ItemType;
            calendarItem.DetermineItemType();

            if (oldItemType != calendarItem.ItemType)
            {
                await Connection.UpdateAsync(calendarItem);
                updatedCount++;
            }
        }

        return updatedCount;
    }


    /// <summary>
    /// Inserts a new attendee
    /// </summary>
    /// <param name="attendee">The attendee to insert</param>
    /// <returns>Number of rows affected</returns>
    public async Task<int> InsertAttendeeAsync(CalendarEventAttendee attendee)
    {
        attendee.Id = Guid.NewGuid();
        attendee.CreatedDate = DateTime.UtcNow;
        attendee.LastModified = DateTime.UtcNow;
        return await Connection.InsertAsync(attendee);
    }

    /// <summary>
    /// Updates an existing attendee
    /// </summary>
    /// <param name="attendee">The attendee to update</param>
    /// <returns>Number of rows affected</returns>
    public async Task<int> UpdateAttendeeAsync(CalendarEventAttendee attendee)
    {
        attendee.LastModified = DateTime.UtcNow;
        return await Connection.UpdateAsync(attendee);
    }

    /// <summary>
    /// Syncs attendees for an event (replaces all existing attendees)
    /// </summary>
    /// <param name="eventId">The internal event Guid</param>
    /// <param name="attendees">List of attendees to sync</param>
    /// <returns>Number of attendees synced</returns>
    public async Task<int> SyncAttendeesForEventAsync(Guid eventId, List<CalendarEventAttendee> attendees)
    {
        // Delete existing attendees for this event
        await Connection.Table<CalendarEventAttendee>()
            .Where(a => a.EventId == eventId)
            .DeleteAsync();

        // Insert new attendees
        int syncedCount = 0;
        foreach (var attendee in attendees)
        {
            attendee.EventId = eventId;
            await InsertAttendeeAsync(attendee);
            syncedCount++;
        }

        return syncedCount;
    }

}
