using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Wino.Providers.Gmail.Google.Apis.Calendar.v3.Data;

public sealed class CalendarList
{
    public IList<CalendarListEntry> Items { get; set; }

    public string NextPageToken { get; set; }

    public string NextSyncToken { get; set; }
}

public sealed class CalendarListEntry
{
    public string AccessRole { get; set; }

    public string BackgroundColor { get; set; }

    public string ColorId { get; set; }

    public bool? Deleted { get; set; }

    public string Description { get; set; }

    public string ForegroundColor { get; set; }

    public bool? Hidden { get; set; }

    public string Id { get; set; }

    public bool? Primary { get; set; }

    public bool? Selected { get; set; }

    public string Summary { get; set; }

    public string SummaryOverride { get; set; }

    public string TimeZone { get; set; }
}

public sealed class Event
{
    public IList<EventAttachment> Attachments { get; set; }

    public IList<EventAttendee> Attendees { get; set; }

    /// <summary>
    /// When updating an event, indicates that the attendee list is intentionally partial.
    /// This allows an attendee to update only their own response without replacing guests.
    /// </summary>
    public bool? AttendeesOmitted { get; set; }

    public string Description { get; set; }

    public EventDateTime End { get; set; }

    public string HtmlLink { get; set; }

    public string HangoutLink { get; set; }

    public ConferenceData ConferenceData { get; set; }

    public string ICalUID { get; set; }

    public string Id { get; set; }

    public string Location { get; set; }

    public bool? Locked { get; set; }

    public EventOrganizer Organizer { get; set; }

    public EventDateTime OriginalStartTime { get; set; }

    public IList<string> Recurrence { get; set; }

    public string RecurringEventId { get; set; }

    public RemindersData Reminders { get; set; }

    public int? Sequence { get; set; }

    public EventDateTime Start { get; set; }

    public string Status { get; set; }

    public string Summary { get; set; }

    public string Transparency { get; set; }

    public string Visibility { get; set; }

    public sealed class RemindersData
    {
        public IList<EventReminder> Overrides { get; set; }

        public bool? UseDefault { get; set; }
    }
}

public sealed class ConferenceData
{
    public CreateConferenceRequest CreateRequest { get; set; }

    public IList<EntryPoint> EntryPoints { get; set; }
}

public sealed class CreateConferenceRequest
{
    public ConferenceSolutionKey ConferenceSolutionKey { get; set; }

    public string RequestId { get; set; }
}

public sealed class ConferenceSolutionKey
{
    public string Type { get; set; }
}

public sealed class EntryPoint
{
    public string EntryPointType { get; set; }

    public string Uri { get; set; }
}

public sealed class EventAttachment
{
    public string FileId { get; set; }

    public string FileUrl { get; set; }

    public string IconLink { get; set; }

    public string MimeType { get; set; }

    public string Title { get; set; }
}

public sealed class EventAttendee
{
    public string Comment { get; set; }

    public string DisplayName { get; set; }

    public string Email { get; set; }

    public bool? Optional { get; set; }

    public bool? Organizer { get; set; }

    public string ResponseStatus { get; set; }

    public bool? Self { get; set; }
}

public sealed class EventDateTime
{
    public string Date { get; set; }

    [JsonPropertyName("dateTime")]
    public DateTimeOffset? DateTimeDateTimeOffset { get; set; }

    public string TimeZone { get; set; }
}

public sealed class EventOrganizer
{
    public string DisplayName { get; set; }

    public string Email { get; set; }

    public bool? Self { get; set; }
}

public sealed class EventReminder
{
    public string Method { get; set; }

    public int? Minutes { get; set; }
}

public sealed class Events
{
    public IList<Event> Items { get; set; }

    public string NextPageToken { get; set; }

    public string NextSyncToken { get; set; }

    public string TimeZone { get; set; }
}
