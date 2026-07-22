using System;
using System.Net.Http;
using Google.Apis.Calendar.v3.Data;
using Wino.Core.Google;

namespace Wino.Core.Google
{
    public sealed class CalendarService : IDisposable, IGoogleBatchService
    {
        public CalendarService(HttpClient httpClient)
        {
            HttpClient = httpClient;
            CalendarList = new global::Google.Apis.Calendar.v3.CalendarListResource(httpClient, this);
            Events = new global::Google.Apis.Calendar.v3.EventsResource(httpClient, this);
        }

        public global::Google.Apis.Calendar.v3.CalendarListResource CalendarList { get; }

        public global::Google.Apis.Calendar.v3.EventsResource Events { get; }

        public HttpClient HttpClient { get; }

        public string BatchEndpoint => "https://www.googleapis.com/batch/calendar/v3";

        public void Dispose()
        {
        }
    }
}

namespace Google.Apis.Calendar.v3
{
    public sealed class CalendarListResource
    {
        private const string BaseUri = "https://www.googleapis.com/calendar/v3/users/me/calendarList";
        private readonly HttpClient _httpClient;
        private readonly object _service;

        internal CalendarListResource(HttpClient httpClient, object service)
        {
            _httpClient = httpClient;
            _service = service;
        }

        public ListRequest List() => new(_httpClient, _service);

        public sealed class ListRequest : GoogleApiRequest<Data.CalendarList>
        {
            internal ListRequest(HttpClient httpClient, object service)
                : base(
                    httpClient,
                    service,
                    HttpMethod.Get,
                    () => BaseUri,
                    GoogleApiJsonContext.Default.CalendarList)
            {
            }
        }
    }

    public sealed class EventsResource
    {
        private const string BaseUri = "https://www.googleapis.com/calendar/v3/calendars";
        private readonly HttpClient _httpClient;
        private readonly object _service;

        internal EventsResource(HttpClient httpClient, object service)
        {
            _httpClient = httpClient;
            _service = service;
        }

        public DeleteRequest Delete(string calendarId, string eventId)
            => new(_httpClient, _service, calendarId, eventId);

        public GetRequest Get(string calendarId, string eventId)
            => new(_httpClient, _service, calendarId, eventId);

        public InsertRequest Insert(Event body, string calendarId)
            => new(_httpClient, _service, body, calendarId);

        public ListRequest List(string calendarId)
            => new(_httpClient, _service, calendarId);

        public PatchRequest Patch(Event body, string calendarId, string eventId)
            => new(_httpClient, _service, body, calendarId, eventId);

        public UpdateRequest Update(Event body, string calendarId, string eventId)
            => new(_httpClient, _service, body, calendarId, eventId);

        private static string EventUri(string calendarId, string eventId)
            => $"{BaseUri}/{GoogleUrl.Segment(calendarId)}/events/{GoogleUrl.Segment(eventId)}";

        private static string EventsUri(string calendarId)
            => $"{BaseUri}/{GoogleUrl.Segment(calendarId)}/events";

        private static string SendUpdatesValue<T>(T value) where T : struct, Enum
            => value.ToString() switch
            {
                "ExternalOnly" => "externalOnly",
                "None" => "none",
                _ => "all"
            };

        public sealed class DeleteRequest : GoogleApiRequest<GoogleEmptyResponse>
        {
            private readonly string _calendarId;
            private readonly string _eventId;

            internal DeleteRequest(HttpClient httpClient, object service, string calendarId, string eventId)
                : base(
                    httpClient,
                    service,
                    HttpMethod.Delete,
                    () => string.Empty,
                    GoogleApiJsonContext.Default.GoogleEmptyResponse)
            {
                _calendarId = calendarId;
                _eventId = eventId;
                RequestUriFactory = () => GoogleUrl.AddQuery(
                    EventUri(_calendarId, _eventId),
                    ("sendUpdates", SendUpdatesValue(SendUpdates)));
            }

            public SendUpdatesEnum SendUpdates { get; set; } = SendUpdatesEnum.None;

            public enum SendUpdatesEnum
            {
                All,
                ExternalOnly,
                None
            }
        }

        public sealed class GetRequest : GoogleApiRequest<Event>
        {
            internal GetRequest(HttpClient httpClient, object service, string calendarId, string eventId)
                : base(
                    httpClient,
                    service,
                    HttpMethod.Get,
                    () => EventUri(calendarId, eventId),
                    GoogleApiJsonContext.Default.Event)
            {
            }
        }

        public sealed class InsertRequest : GoogleApiRequest<Event>
        {
            private readonly string _calendarId;

            internal InsertRequest(HttpClient httpClient, object service, Event body, string calendarId)
                : base(
                    httpClient,
                    service,
                    HttpMethod.Post,
                    () => string.Empty,
                    GoogleApiJsonContext.Default.Event,
                    () => GoogleJsonContent.Create(body, GoogleApiJsonContext.Default.Event))
            {
                _calendarId = calendarId;
                RequestUriFactory = () => GoogleUrl.AddQuery(
                    EventsUri(_calendarId),
                    ("sendUpdates", SendUpdatesValue(SendUpdates)),
                    ("supportsAttachments", GoogleUrl.Boolean(SupportsAttachments)));
            }

            public SendUpdatesEnum SendUpdates { get; set; } = SendUpdatesEnum.None;

            public bool? SupportsAttachments { get; set; }

            public enum SendUpdatesEnum
            {
                All,
                ExternalOnly,
                None
            }
        }

        public sealed class ListRequest : GoogleApiRequest<Data.Events>
        {
            private readonly string _calendarId;

            internal ListRequest(HttpClient httpClient, object service, string calendarId)
                : base(
                    httpClient,
                    service,
                    HttpMethod.Get,
                    () => string.Empty,
                    GoogleApiJsonContext.Default.Events)
            {
                _calendarId = calendarId;
                RequestUriFactory = BuildRequestUri;
            }

            public string ICalUID { get; set; }

            public long? MaxResults { get; set; }

            public string PageToken { get; set; }

            public bool? ShowDeleted { get; set; }

            public bool? SingleEvents { get; set; }

            public string SyncToken { get; set; }

            public DateTimeOffset? TimeMinDateTimeOffset { get; set; }

            private string BuildRequestUri() => GoogleUrl.AddQuery(
                EventsUri(_calendarId),
                ("iCalUID", ICalUID),
                ("maxResults", GoogleUrl.Number(MaxResults)),
                ("pageToken", PageToken),
                ("showDeleted", GoogleUrl.Boolean(ShowDeleted)),
                ("singleEvents", GoogleUrl.Boolean(SingleEvents)),
                ("syncToken", SyncToken),
                ("timeMin", TimeMinDateTimeOffset?.ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
        }

        public sealed class PatchRequest : GoogleApiRequest<Event>
        {
            private readonly string _calendarId;
            private readonly string _eventId;

            internal PatchRequest(HttpClient httpClient, object service, Event body, string calendarId, string eventId)
                : base(
                    httpClient,
                    service,
                    HttpMethod.Patch,
                    () => string.Empty,
                    GoogleApiJsonContext.Default.Event,
                    () => GoogleJsonContent.Create(body, GoogleApiJsonContext.Default.Event))
            {
                _calendarId = calendarId;
                _eventId = eventId;
                RequestUriFactory = () => GoogleUrl.AddQuery(
                    EventUri(_calendarId, _eventId),
                    ("sendUpdates", SendUpdatesValue(SendUpdates)),
                    ("supportsAttachments", GoogleUrl.Boolean(SupportsAttachments)));
            }

            public SendUpdatesEnum SendUpdates { get; set; } = SendUpdatesEnum.None;

            public bool? SupportsAttachments { get; set; }

            public enum SendUpdatesEnum
            {
                All,
                ExternalOnly,
                None
            }
        }

        public sealed class UpdateRequest : GoogleApiRequest<Event>
        {
            private readonly string _calendarId;
            private readonly string _eventId;

            internal UpdateRequest(HttpClient httpClient, object service, Event body, string calendarId, string eventId)
                : base(
                    httpClient,
                    service,
                    HttpMethod.Put,
                    () => string.Empty,
                    GoogleApiJsonContext.Default.Event,
                    () => GoogleJsonContent.Create(body, GoogleApiJsonContext.Default.Event))
            {
                _calendarId = calendarId;
                _eventId = eventId;
                RequestUriFactory = () => GoogleUrl.AddQuery(
                    EventUri(_calendarId, _eventId),
                    ("sendUpdates", SendUpdatesValue(SendUpdates)),
                    ("supportsAttachments", GoogleUrl.Boolean(SupportsAttachments)));
            }

            public SendUpdatesEnum SendUpdates { get; set; } = SendUpdatesEnum.None;

            public bool? SupportsAttachments { get; set; }

            public enum SendUpdatesEnum
            {
                All,
                ExternalOnly,
                None
            }
        }
    }
}
