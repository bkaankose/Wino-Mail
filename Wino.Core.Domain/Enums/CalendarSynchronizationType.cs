namespace Wino.Core.Domain.Enums
{
    public enum CalendarSynchronizationType
    {
        ExecuteRequests, // Execute all requests in the queue.
        CalendarMetadata, // Sync calendar metadata.
        CalendarEvents, // Sync all events for all calendars.
        SingleCalendar, // Sync events for only specified calendars.
        UpdateProfile // Update profile information only.
    }
}
