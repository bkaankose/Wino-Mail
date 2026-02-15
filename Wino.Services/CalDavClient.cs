using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Services;

public sealed class CalDavClient : ICalDavClient
{
    private static readonly HttpMethod PropFindMethod = new("PROPFIND");
    private static readonly HttpMethod ReportMethod = new("REPORT");
    private static readonly ILogger Logger = Log.ForContext<CalDavClient>();

    private readonly HttpClient _httpClient;

    public CalDavClient(HttpClient httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<IReadOnlyList<CalDavCalendar>> DiscoverCalendarsAsync(
        CalDavConnectionSettings connectionSettings,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionSettings(connectionSettings);

        var principalUri = await DiscoverPrincipalUriAsync(connectionSettings, cancellationToken).ConfigureAwait(false);
        var homeSetUri = await DiscoverCalendarHomeSetUriAsync(connectionSettings, principalUri, cancellationToken).ConfigureAwait(false);

        var body = """
            <D:propfind xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav" xmlns:CS="http://calendarserver.org/ns/">
              <D:prop>
                <D:resourcetype />
                <D:displayname />
                <CS:getctag />
                <D:sync-token />
              </D:prop>
            </D:propfind>
            """;

        var responseXml = await SendXmlAsync(
            connectionSettings,
            PropFindMethod,
            homeSetUri,
            depth: "1",
            body,
            cancellationToken).ConfigureAwait(false);

        var calendars = ParseCalendarCollection(responseXml, homeSetUri)
            .GroupBy(c => c.RemoteCalendarId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        return calendars;
    }

    public async Task<IReadOnlyList<CalDavCalendarEvent>> GetCalendarEventsAsync(
        CalDavConnectionSettings connectionSettings,
        CalDavCalendar calendar,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionSettings(connectionSettings);

        if (calendar == null || string.IsNullOrWhiteSpace(calendar.RemoteCalendarId))
            return [];

        var calendarUri = new Uri(calendar.RemoteCalendarId);
        var startString = startUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");
        var endString = endUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");

        var body = $"""
            <C:calendar-query xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:prop>
                <D:getetag />
                <C:calendar-data />
              </D:prop>
              <C:filter>
                <C:comp-filter name="VCALENDAR">
                  <C:comp-filter name="VEVENT">
                    <C:time-range start="{startString}" end="{endString}" />
                  </C:comp-filter>
                </C:comp-filter>
              </C:filter>
            </C:calendar-query>
            """;

        var responseXml = await SendXmlAsync(
            connectionSettings,
            ReportMethod,
            calendarUri,
            depth: "1",
            body,
            cancellationToken).ConfigureAwait(false);

        var eventResponses = ParseEventResponses(responseXml, calendarUri);
        var result = new List<CalDavCalendarEvent>();

        foreach (var eventResponse in eventResponses)
        {
            result.AddRange(ParseCalendarData(
                eventResponse.CalendarData,
                eventResponse.Href,
                eventResponse.ETag,
                startUtc,
                endUtc));
        }

        // Ensure recurring parents are saved before child occurrences/exceptions.
        return result
            .OrderByDescending(e => e.IsSeriesMaster)
            .ThenBy(e => e.Start)
            .ToList();
    }

    private static void ValidateConnectionSettings(CalDavConnectionSettings connectionSettings)
    {
        if (connectionSettings?.ServiceUri == null)
            throw new ArgumentException("Service URI is required for CalDAV.");

        if (string.IsNullOrWhiteSpace(connectionSettings.Username))
            throw new ArgumentException("Username is required for CalDAV.");

        if (string.IsNullOrWhiteSpace(connectionSettings.Password))
            throw new ArgumentException("Password is required for CalDAV.");
    }

    private async Task<Uri> DiscoverPrincipalUriAsync(CalDavConnectionSettings connectionSettings, CancellationToken cancellationToken)
    {
        var body = """
            <D:propfind xmlns:D="DAV:">
              <D:prop>
                <D:current-user-principal />
              </D:prop>
            </D:propfind>
            """;

        var responseXml = await SendXmlAsync(
            connectionSettings,
            PropFindMethod,
            connectionSettings.ServiceUri,
            depth: "0",
            body,
            cancellationToken).ConfigureAwait(false);

        var principalHref = responseXml
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "current-user-principal")
            ?.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "href")
            ?.Value;

        return string.IsNullOrWhiteSpace(principalHref)
            ? connectionSettings.ServiceUri
            : CreateAbsoluteUri(connectionSettings.ServiceUri, principalHref);
    }

    private async Task<Uri> DiscoverCalendarHomeSetUriAsync(
        CalDavConnectionSettings connectionSettings,
        Uri principalUri,
        CancellationToken cancellationToken)
    {
        var body = """
            <D:propfind xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:prop>
                <C:calendar-home-set />
              </D:prop>
            </D:propfind>
            """;

        var responseXml = await SendXmlAsync(
            connectionSettings,
            PropFindMethod,
            principalUri,
            depth: "0",
            body,
            cancellationToken).ConfigureAwait(false);

        var homeSetHref = responseXml
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "calendar-home-set")
            ?.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "href")
            ?.Value;

        return string.IsNullOrWhiteSpace(homeSetHref)
            ? principalUri
            : CreateAbsoluteUri(principalUri, homeSetHref);
    }

    private async Task<XDocument> SendXmlAsync(
        CalDavConnectionSettings connectionSettings,
        HttpMethod method,
        Uri uri,
        string depth,
        string body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);

        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{connectionSettings.Username}:{connectionSettings.Password}")));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));
        request.Headers.Add("Depth", depth);
        request.Content = new StringContent(body, Encoding.UTF8, "application/xml");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException("CalDAV authorization failed.");
        }

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.MultiStatus)
        {
            var failureBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"CalDAV request failed ({(int)response.StatusCode}): {failureBody}");
        }

        var xml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(xml))
            return new XDocument(new XElement("empty"));

        return XDocument.Parse(xml);
    }

    private static List<CalDavCalendar> ParseCalendarCollection(XDocument xml, Uri baseUri)
    {
        var result = new List<CalDavCalendar>();

        foreach (var response in xml.Descendants().Where(e => e.Name.LocalName == "response"))
        {
            var href = response.Descendants().FirstOrDefault(e => e.Name.LocalName == "href")?.Value;
            if (string.IsNullOrWhiteSpace(href))
                continue;

            foreach (var prop in GetSuccessProps(response))
            {
                var resourceType = prop.Descendants().FirstOrDefault(e => e.Name.LocalName == "resourcetype");
                if (resourceType == null)
                    continue;

                var isCalendar = resourceType.Descendants().Any(e => e.Name.LocalName == "calendar");
                if (!isCalendar)
                    continue;

                var displayName = prop.Descendants().FirstOrDefault(e => e.Name.LocalName == "displayname")?.Value ?? string.Empty;
                var ctag = prop.Descendants().FirstOrDefault(e => e.Name.LocalName == "getctag")?.Value ?? string.Empty;
                var syncToken = prop.Descendants().FirstOrDefault(e => e.Name.LocalName == "sync-token")?.Value ?? string.Empty;
                var remoteUri = CreateAbsoluteUri(baseUri, href).ToString().TrimEnd('/');

                if (string.IsNullOrWhiteSpace(displayName))
                {
                    displayName = WebUtility.UrlDecode(remoteUri.Split('/').LastOrDefault() ?? "Calendar");
                }

                result.Add(new CalDavCalendar
                {
                    RemoteCalendarId = remoteUri,
                    Name = displayName,
                    CTag = ctag,
                    SyncToken = syncToken
                });
            }
        }

        return result;
    }

    private static IEnumerable<CalDavEventResponse> ParseEventResponses(XDocument xml, Uri baseUri)
    {
        foreach (var response in xml.Descendants().Where(e => e.Name.LocalName == "response"))
        {
            var href = response.Descendants().FirstOrDefault(e => e.Name.LocalName == "href")?.Value;
            if (string.IsNullOrWhiteSpace(href))
                continue;

            foreach (var prop in GetSuccessProps(response))
            {
                var calendarData = prop.Descendants().FirstOrDefault(e => e.Name.LocalName == "calendar-data")?.Value;
                if (string.IsNullOrWhiteSpace(calendarData))
                    continue;

                var eTag = prop.Descendants().FirstOrDefault(e => e.Name.LocalName == "getetag")?.Value ?? string.Empty;

                yield return new CalDavEventResponse(
                    CreateAbsoluteUri(baseUri, href).ToString(),
                    eTag,
                    calendarData);
            }
        }
    }

    private static IEnumerable<XElement> GetSuccessProps(XElement response)
    {
        foreach (var propstat in response.Elements().Where(e => e.Name.LocalName == "propstat"))
        {
            var status = propstat.Elements().FirstOrDefault(e => e.Name.LocalName == "status")?.Value ?? string.Empty;
            if (!status.Contains(" 200 ", StringComparison.Ordinal))
                continue;

            var prop = propstat.Elements().FirstOrDefault(e => e.Name.LocalName == "prop");
            if (prop != null)
                yield return prop;
        }
    }

    private static List<CalDavCalendarEvent> ParseCalendarData(
        string icsContent,
        string resourceHref,
        string eTag,
        DateTimeOffset windowStartUtc,
        DateTimeOffset windowEndUtc)
    {
        try
        {
            var calendar = Calendar.Load(icsContent);
            if (calendar?.Events == null || calendar.Events.Count == 0)
                return [];

            var allEvents = calendar.Events.ToList();
            var result = new List<CalDavCalendarEvent>();

            var masters = allEvents
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Uid) && e.RecurrenceId == null)
                .GroupBy(e => e.Uid, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            var exceptionMap = allEvents
                .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Uid) && e.RecurrenceId != null)
                .GroupBy(e => $"{e.Uid}|{GetOccurrenceKey(e.RecurrenceId)}", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var consumedExceptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var master in masters)
            {
                var masterRemoteId = BuildRemoteEventId(master.Uid, null);
                var hasRecurrence = HasRecurrence(master);

                if (hasRecurrence)
                {
                    result.Add(CreateCalendarEvent(
                        sourceEvent: master,
                        start: ToDateTimeOffset(master.Start),
                        end: ToDateTimeOffset(master.End),
                        remoteEventId: masterRemoteId,
                        resourceHref: resourceHref,
                        eTag: eTag,
                        icsContent: icsContent,
                        isSeriesMaster: true,
                        isRecurringInstance: false,
                        seriesMasterRemoteEventId: string.Empty,
                        recurrence: BuildRecurrenceString(master)));

                    var occurrences = master.GetOccurrences(windowStartUtc.UtcDateTime, windowEndUtc.UtcDateTime);

                    foreach (var occurrence in occurrences)
                    {
                        var key = GetOccurrenceKey(occurrence.Period.StartTime);
                        var mapKey = $"{master.Uid}|{key}";

                        var sourceEvent = exceptionMap.TryGetValue(mapKey, out var exceptionEvent)
                            ? exceptionEvent
                            : master;

                        if (exceptionEvent != null)
                            consumedExceptions.Add(mapKey);

                        var occurrenceStart = ToDateTimeOffset(occurrence.Period.StartTime);
                        var occurrenceEnd = ToDateTimeOffset(occurrence.Period.EndTime);

                        result.Add(CreateCalendarEvent(
                            sourceEvent: sourceEvent,
                            start: occurrenceStart,
                            end: occurrenceEnd,
                            remoteEventId: BuildRemoteEventId(master.Uid, key),
                            resourceHref: resourceHref,
                            eTag: eTag,
                            icsContent: icsContent,
                            isSeriesMaster: false,
                            isRecurringInstance: true,
                            seriesMasterRemoteEventId: masterRemoteId,
                            recurrence: string.Empty));
                    }
                }
                else
                {
                    var start = ToDateTimeOffset(master.Start);
                    var end = ToDateTimeOffset(master.End);

                    if (!Overlaps(start, end, windowStartUtc, windowEndUtc))
                        continue;

                    result.Add(CreateCalendarEvent(
                        sourceEvent: master,
                        start: start,
                        end: end,
                        remoteEventId: masterRemoteId,
                        resourceHref: resourceHref,
                        eTag: eTag,
                        icsContent: icsContent,
                        isSeriesMaster: false,
                        isRecurringInstance: false,
                        seriesMasterRemoteEventId: string.Empty,
                        recurrence: string.Empty));
                }
            }

            foreach (var exceptionEvent in allEvents.Where(e => e != null && e.RecurrenceId != null && !string.IsNullOrWhiteSpace(e.Uid)))
            {
                var key = $"{exceptionEvent.Uid}|{GetOccurrenceKey(exceptionEvent.RecurrenceId)}";
                if (consumedExceptions.Contains(key))
                    continue;

                var start = ToDateTimeOffset(exceptionEvent.Start);
                var end = ToDateTimeOffset(exceptionEvent.End);

                if (!Overlaps(start, end, windowStartUtc, windowEndUtc))
                    continue;

                var masterRemoteId = BuildRemoteEventId(exceptionEvent.Uid, null);

                result.Add(CreateCalendarEvent(
                    sourceEvent: exceptionEvent,
                    start: start,
                    end: end,
                    remoteEventId: BuildRemoteEventId(exceptionEvent.Uid, GetOccurrenceKey(exceptionEvent.RecurrenceId)),
                    resourceHref: resourceHref,
                    eTag: eTag,
                    icsContent: icsContent,
                    isSeriesMaster: false,
                    isRecurringInstance: true,
                    seriesMasterRemoteEventId: masterRemoteId,
                    recurrence: string.Empty));
            }

            return result;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to parse CalDAV ICS payload.");
            return [];
        }
    }

    private static bool HasRecurrence(CalendarEvent calendarEvent)
        => (calendarEvent.RecurrenceRules?.Any() ?? false)
           || (calendarEvent.RecurrenceDates?.Any() ?? false);

    private static string BuildRemoteEventId(string uid, string occurrenceKey)
        => string.IsNullOrWhiteSpace(occurrenceKey) ? uid : $"{uid}::{occurrenceKey}";

    private static string GetOccurrenceKey(IDateTime dateTime)
        => dateTime.AsUtc.ToString("yyyyMMdd'T'HHmmss'Z'");

    private static DateTimeOffset ToDateTimeOffset(IDateTime dateTime)
        => dateTime?.AsDateTimeOffset ?? default;

    private static bool Overlaps(DateTimeOffset start, DateTimeOffset end, DateTimeOffset windowStart, DateTimeOffset windowEnd)
    {
        if (end <= start)
            end = start.AddHours(1);

        return start < windowEnd && end > windowStart;
    }

    private static CalDavCalendarEvent CreateCalendarEvent(
        CalendarEvent sourceEvent,
        DateTimeOffset start,
        DateTimeOffset end,
        string remoteEventId,
        string resourceHref,
        string eTag,
        string icsContent,
        bool isSeriesMaster,
        bool isRecurringInstance,
        string seriesMasterRemoteEventId,
        string recurrence)
    {
        if (end <= start)
            end = start.AddHours(1);

        var status = MapStatus(sourceEvent.Status);
        var attendees = sourceEvent.Attendees?
            .Where(a => a != null && a.Value != null)
            .Select(a => new CalDavEventAttendee
            {
                Name = a.CommonName ?? string.Empty,
                Email = NormalizeCalendarEmail(a.Value),
                AttendenceStatus = MapAttendeeStatus(a.ParticipationStatus),
                IsOrganizer = string.Equals(a.Role, "CHAIR", StringComparison.OrdinalIgnoreCase),
                IsOptionalAttendee = string.Equals(a.Role, "OPT-PARTICIPANT", StringComparison.OrdinalIgnoreCase)
            })
            .Where(a => !string.IsNullOrWhiteSpace(a.Email))
            .ToList() ?? [];

        var reminders = sourceEvent.Alarms?
            .Where(a => a?.Trigger != null && a.Trigger.IsRelative && a.Trigger.Duration.HasValue)
            .Select(a => new CalDavEventReminder
            {
                DurationInSeconds = (int)Math.Abs(a.Trigger.Duration.Value.TotalSeconds),
                ReminderType = string.Equals(a.Action, "EMAIL", StringComparison.OrdinalIgnoreCase)
                    ? CalendarItemReminderType.Email
                    : CalendarItemReminderType.Popup
            })
            .Where(r => r.DurationInSeconds > 0)
            .ToList() ?? [];

        return new CalDavCalendarEvent
        {
            RemoteEventId = remoteEventId,
            RemoteResourceHref = resourceHref,
            ETag = eTag,
            IcsContent = icsContent,
            Uid = sourceEvent.Uid ?? string.Empty,
            SeriesMasterRemoteEventId = seriesMasterRemoteEventId,
            IsSeriesMaster = isSeriesMaster,
            IsRecurringInstance = isRecurringInstance,
            Title = sourceEvent.Summary ?? string.Empty,
            Description = sourceEvent.Description ?? string.Empty,
            Location = sourceEvent.Location ?? string.Empty,
            Start = start,
            End = end,
            StartTimeZone = sourceEvent.Start?.TzId ?? string.Empty,
            EndTimeZone = sourceEvent.End?.TzId ?? string.Empty,
            Recurrence = recurrence,
            OrganizerDisplayName = sourceEvent.Organizer?.CommonName ?? string.Empty,
            OrganizerEmail = NormalizeCalendarEmail(sourceEvent.Organizer?.Value),
            Status = status,
            Visibility = MapVisibility(sourceEvent.Class),
            ShowAs = MapShowAs(sourceEvent.Transparency),
            IsHidden = status == CalendarItemStatus.Cancelled,
            Attendees = attendees,
            Reminders = reminders
        };
    }

    private static string BuildRecurrenceString(CalendarEvent sourceEvent)
    {
        var recurrenceLines = new List<string>();

        if (sourceEvent.RecurrenceRules != null)
        {
            recurrenceLines.AddRange(sourceEvent.RecurrenceRules.Select(r => $"RRULE:{r}"));
        }

        if (sourceEvent.ExceptionDates != null)
        {
            foreach (var periodList in sourceEvent.ExceptionDates)
            {
                var dates = periodList
                    .Where(p => p.StartTime != null)
                    .Select(p => p.StartTime.AsUtc.ToString("yyyyMMdd'T'HHmmss'Z'"))
                    .ToList();

                if (dates.Count > 0)
                {
                    recurrenceLines.Add($"EXDATE:{string.Join(",", dates)}");
                }
            }
        }

        if (sourceEvent.RecurrenceDates != null)
        {
            foreach (var periodList in sourceEvent.RecurrenceDates)
            {
                var dates = periodList
                    .Where(p => p.StartTime != null)
                    .Select(p => p.StartTime.AsUtc.ToString("yyyyMMdd'T'HHmmss'Z'"))
                    .ToList();

                if (dates.Count > 0)
                {
                    recurrenceLines.Add($"RDATE:{string.Join(",", dates)}");
                }
            }
        }

        return recurrenceLines.Count == 0
            ? string.Empty
            : string.Join(Constants.CalendarEventRecurrenceRuleSeperator, recurrenceLines);
    }

    private static CalendarItemStatus MapStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return CalendarItemStatus.Accepted;

        if (string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
            return CalendarItemStatus.Cancelled;

        if (string.Equals(status, "TENTATIVE", StringComparison.OrdinalIgnoreCase))
            return CalendarItemStatus.Tentative;

        return CalendarItemStatus.Accepted;
    }

    private static CalendarItemVisibility MapVisibility(string classValue)
    {
        if (string.IsNullOrWhiteSpace(classValue))
            return CalendarItemVisibility.Default;

        return classValue.ToUpperInvariant() switch
        {
            "PUBLIC" => CalendarItemVisibility.Public,
            "PRIVATE" => CalendarItemVisibility.Private,
            "CONFIDENTIAL" => CalendarItemVisibility.Confidential,
            _ => CalendarItemVisibility.Default
        };
    }

    private static CalendarItemShowAs MapShowAs(string transparency)
    {
        if (string.Equals(transparency, "TRANSPARENT", StringComparison.OrdinalIgnoreCase))
            return CalendarItemShowAs.Free;

        return CalendarItemShowAs.Busy;
    }

    private static AttendeeStatus MapAttendeeStatus(string participationStatus)
    {
        if (string.IsNullOrWhiteSpace(participationStatus))
            return AttendeeStatus.NeedsAction;

        return participationStatus.ToUpperInvariant() switch
        {
            "ACCEPTED" => AttendeeStatus.Accepted,
            "DECLINED" => AttendeeStatus.Declined,
            "TENTATIVE" => AttendeeStatus.Tentative,
            _ => AttendeeStatus.NeedsAction
        };
    }

    private static string NormalizeCalendarEmail(Uri emailUri)
    {
        if (emailUri == null)
            return string.Empty;

        var value = emailUri.OriginalString;
        if (value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            value = value[7..];

        return value;
    }

    private static Uri CreateAbsoluteUri(Uri baseUri, string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
            return absolute;

        return new Uri(baseUri, href);
    }

    private sealed record CalDavEventResponse(string Href, string ETag, string CalendarData);
}

