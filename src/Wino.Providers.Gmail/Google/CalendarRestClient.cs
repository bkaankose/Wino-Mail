using System;
using System.Globalization;
using System.Net.Http;
using Wino.Providers.Gmail.Google.Apis.Calendar.v3.Data;

namespace Wino.Providers.Gmail.Google;

public sealed class CalendarService : IDisposable, IGoogleBatchService
{
    public CalendarService(HttpClient httpClient)
    {
        HttpClient = httpClient;
        CalendarList = new global::Wino.Providers.Gmail.Google.Apis.Calendar.v3.CalendarListResource(httpClient, this);
        Events = new global::Wino.Providers.Gmail.Google.Apis.Calendar.v3.EventsResource(httpClient, this);
    }

    public global::Wino.Providers.Gmail.Google.Apis.Calendar.v3.CalendarListResource CalendarList { get; }

    public global::Wino.Providers.Gmail.Google.Apis.Calendar.v3.EventsResource Events { get; }

    public HttpClient HttpClient { get; }

    public string BatchEndpoint => "https://www.googleapis.com/batch/calendar/v3";

    public void Dispose()
    {
    }
}
