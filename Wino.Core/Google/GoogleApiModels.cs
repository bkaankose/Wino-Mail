using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Google
{
    public sealed class GoogleApiException : Exception
    {
        public GoogleApiException(System.Net.HttpStatusCode httpStatusCode, GoogleApiError error)
            : base(error?.Message ?? $"Google API request failed with HTTP {(int)httpStatusCode}.")
        {
            HttpStatusCode = httpStatusCode;
            Error = error;
        }

        public System.Net.HttpStatusCode HttpStatusCode { get; }

        public GoogleApiError Error { get; }
    }

    public sealed class GoogleApiErrorEnvelope
    {
        public GoogleApiError Error { get; set; }
    }

    public sealed class GoogleApiError
    {
        public int Code { get; set; }

        public string Message { get; set; }

        public string Status { get; set; }

        public List<GoogleApiErrorDetail> Errors { get; set; }
    }

    public sealed class GoogleApiErrorDetail
    {
        public string Domain { get; set; }

        public string Message { get; set; }

        public string Reason { get; set; }

        public override string ToString() => Message ?? Reason ?? string.Empty;
    }
}

namespace Google.Apis.Util
{
    /// <summary>
    /// Source-compatible collection used by the old Google SDK call sites. The REST client
    /// serializes every item as a repeated query parameter and does not use reflection.
    /// </summary>
    public sealed class Repeatable<T> : List<T>
    {
        public Repeatable(IEnumerable<T> values) : base(values)
        {
        }
    }
}

namespace Google.Apis.Gmail.v1.Data
{
    public sealed class BatchDeleteMessagesRequest
    {
        public IList<string> Ids { get; set; }
    }

    public sealed class BatchModifyMessagesRequest
    {
        public IList<string> AddLabelIds { get; set; }

        public IList<string> Ids { get; set; }

        public IList<string> RemoveLabelIds { get; set; }
    }

    public sealed class Draft
    {
        public string Id { get; set; }

        public Message Message { get; set; }
    }

    public sealed class History
    {
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public ulong? Id { get; set; }

        public IList<HistoryLabelAdded> LabelsAdded { get; set; }

        public IList<HistoryLabelRemoved> LabelsRemoved { get; set; }

        public IList<Message> Messages { get; set; }

        public IList<HistoryMessageAdded> MessagesAdded { get; set; }

        public IList<HistoryMessageDeleted> MessagesDeleted { get; set; }
    }

    public sealed class HistoryLabelAdded
    {
        public IList<string> LabelIds { get; set; }

        public Message Message { get; set; }
    }

    public sealed class HistoryLabelRemoved
    {
        public IList<string> LabelIds { get; set; }

        public Message Message { get; set; }
    }

    public sealed class HistoryMessageAdded
    {
        public Message Message { get; set; }
    }

    public sealed class HistoryMessageDeleted
    {
        public Message Message { get; set; }
    }

    public sealed class Label
    {
        public LabelColor Color { get; set; }

        public string Id { get; set; }

        public string LabelListVisibility { get; set; }

        public string MessageListVisibility { get; set; }

        public long? MessagesTotal { get; set; }

        public long? MessagesUnread { get; set; }

        public string Name { get; set; }

        public long? ThreadsTotal { get; set; }

        public long? ThreadsUnread { get; set; }

        public string Type { get; set; }
    }

    public sealed class LabelColor
    {
        public string BackgroundColor { get; set; }

        public string TextColor { get; set; }
    }

    public sealed class ListDraftsResponse
    {
        public IList<Draft> Drafts { get; set; }

        public string NextPageToken { get; set; }

        public long? ResultSizeEstimate { get; set; }
    }

    public sealed class ListHistoryResponse
    {
        public IList<History> History { get; set; }

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public ulong? HistoryId { get; set; }

        public string NextPageToken { get; set; }
    }

    public sealed class ListLabelsResponse
    {
        public IList<Label> Labels { get; set; }
    }

    public sealed class Filter
    {
        public FilterAction Action { get; set; }

        public FilterCriteria Criteria { get; set; }

        public string Id { get; set; }
    }

    public sealed class FilterAction
    {
        public IList<string> AddLabelIds { get; set; }

        public string Forward { get; set; }

        public IList<string> RemoveLabelIds { get; set; }
    }

    public sealed class FilterCriteria
    {
        public bool? ExcludeChats { get; set; }

        public string From { get; set; }

        public bool? HasAttachment { get; set; }

        public string NegatedQuery { get; set; }

        public string Query { get; set; }

        public long? Size { get; set; }

        public string SizeComparison { get; set; }

        public string Subject { get; set; }

        public string To { get; set; }
    }

    public sealed class ListFiltersResponse
    {
        [JsonPropertyName("filter")]
        public IList<Filter> Filter { get; set; }
    }

    public sealed class ListMessagesResponse
    {
        public IList<Message> Messages { get; set; }

        public string NextPageToken { get; set; }

        public long? ResultSizeEstimate { get; set; }
    }

    public sealed class ListSendAsResponse
    {
        public IList<SendAs> SendAs { get; set; }
    }

    public sealed class Message
    {
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public ulong? HistoryId { get; set; }

        public string Id { get; set; }

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public long? InternalDate { get; set; }

        public IList<string> LabelIds { get; set; }

        public MessagePart Payload { get; set; }

        public string Raw { get; set; }

        public int? SizeEstimate { get; set; }

        public string Snippet { get; set; }

        public string ThreadId { get; set; }
    }

    public sealed class MessagePart
    {
        public MessagePartBody Body { get; set; }

        public string Filename { get; set; }

        public IList<MessagePartHeader> Headers { get; set; }

        public string MimeType { get; set; }

        public string PartId { get; set; }

        public IList<MessagePart> Parts { get; set; }
    }

    public sealed class MessagePartBody
    {
        public string AttachmentId { get; set; }

        public string Data { get; set; }

        public int? Size { get; set; }
    }

    public sealed class MessagePartHeader
    {
        public string Name { get; set; }

        public string Value { get; set; }
    }

    public sealed class Profile
    {
        public string EmailAddress { get; set; }

        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public ulong? HistoryId { get; set; }

        public long? MessagesTotal { get; set; }

        public long? ThreadsTotal { get; set; }
    }

    public sealed class SendAs
    {
        public string DisplayName { get; set; }

        public bool? IsDefault { get; set; }

        public bool? IsPrimary { get; set; }

        public string ReplyToAddress { get; set; }

        public string SendAsEmail { get; set; }

        public string Signature { get; set; }

        public string VerificationStatus { get; set; }
    }
}

namespace Google.Apis.Calendar.v3.Data
{
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
}

namespace Google.Apis.PeopleService.v1.Data
{
    public sealed class EmailAddress
    {
        public FieldMetadata Metadata { get; set; }

        public string Value { get; set; }
    }

    public sealed class FieldMetadata
    {
        public bool? Primary { get; set; }
    }

    public sealed class Name
    {
        public string DisplayName { get; set; }

        public FieldMetadata Metadata { get; set; }
    }

    public sealed class Person
    {
        public IList<EmailAddress> EmailAddresses { get; set; }

        public IList<Name> Names { get; set; }

        public IList<Photo> Photos { get; set; }
    }

    public sealed class Photo
    {
        public FieldMetadata Metadata { get; set; }

        public string Url { get; set; }
    }
}

namespace Google.Apis.Drive.v3.Data
{
    public sealed class File
    {
        public string Id { get; set; }

        public string MimeType { get; set; }

        public string Name { get; set; }

        public IList<string> Parents { get; set; }

        public string WebViewLink { get; set; }
    }
}
