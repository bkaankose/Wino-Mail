using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Wino.Core.Domain.Models.Calendar;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public sealed class CalDavServiceLiveTests
{
    private const string ManualSkipMessage = "Manual live CalDAV test. Fill ServiceUri/Username/Password placeholders and remove Skip to run.";

    // Replace placeholders with your own credentials when running these live tests.
    private const string ServiceUri = "https://caldav.icloud.com/";
    private const string Username = "REPLACE_WITH_USERNAME";
    private const string Password = "REPLACE_WITH_PASSWORD";

    private static readonly DateTimeOffset SyncWindowStartUtc = new(2026, 01, 01, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset SyncWindowEndUtc = new(2026, 12, 31, 23, 59, 59, TimeSpan.Zero);

    [Fact(Skip = ManualSkipMessage)]
    [Trait("Category", "Live")]
    public async Task InitialSync_ReturnsCalendarEvents()
    {
        var client = new CalDavClient();
        var settings = BuildConnectionSettings();

        var calendars = await client.DiscoverCalendarsAsync(settings);
        calendars.Should().NotBeEmpty();

        var calendar = calendars.First();
        var events = await client.GetCalendarEventsAsync(settings, calendar, SyncWindowStartUtc, SyncWindowEndUtc);

        events.Should().NotBeNull();
    }

    [Fact(Skip = ManualSkipMessage)]
    [Trait("Category", "Live")]
    public async Task AddThenRemoveEvent_ChangesServerState()
    {
        var client = new CalDavClient();
        var settings = BuildConnectionSettings();
        var calendar = await GetTargetCalendarAsync(client, settings);

        var eventId = $"wino-live-add-delete-{Guid.NewGuid():N}";
        var resourceUri = BuildEventResourceUri(calendar, eventId);

        await PutEventAsync(settings, resourceUri, BuildIcs(eventId, "Wino Live Add/Delete", new DateTimeOffset(2026, 04, 01, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 04, 01, 11, 0, 0, TimeSpan.Zero)));

        var afterAdd = await client.GetCalendarEventsAsync(settings, calendar, SyncWindowStartUtc, SyncWindowEndUtc);
        afterAdd.Should().Contain(e => e.Uid == eventId);

        await DeleteEventAsync(settings, resourceUri);

        var afterDelete = await client.GetCalendarEventsAsync(settings, calendar, SyncWindowStartUtc, SyncWindowEndUtc);
        afterDelete.Should().NotContain(e => e.Uid == eventId);
    }

    [Fact(Skip = ManualSkipMessage)]
    [Trait("Category", "Live")]
    public async Task UpdateExistingEvent_ChangesStartAndEndDates()
    {
        var client = new CalDavClient();
        var settings = BuildConnectionSettings();
        var calendar = await GetTargetCalendarAsync(client, settings);

        var eventId = $"wino-live-update-{Guid.NewGuid():N}";
        var resourceUri = BuildEventResourceUri(calendar, eventId);

        var initialStart = new DateTimeOffset(2026, 05, 01, 8, 0, 0, TimeSpan.Zero);
        var initialEnd = new DateTimeOffset(2026, 05, 01, 9, 0, 0, TimeSpan.Zero);

        await PutEventAsync(settings, resourceUri, BuildIcs(eventId, "Wino Live Update", initialStart, initialEnd));

        var updatedStart = new DateTimeOffset(2026, 05, 02, 14, 30, 0, TimeSpan.Zero);
        var updatedEnd = new DateTimeOffset(2026, 05, 02, 16, 0, 0, TimeSpan.Zero);

        await PutEventAsync(settings, resourceUri, BuildIcs(eventId, "Wino Live Update", updatedStart, updatedEnd));

        var events = await client.GetCalendarEventsAsync(settings, calendar, SyncWindowStartUtc, SyncWindowEndUtc);
        var updatedEvent = events.First(e => e.Uid == eventId);

        updatedEvent.Start.Should().Be(updatedStart);
        updatedEvent.End.Should().Be(updatedEnd);

        await DeleteEventAsync(settings, resourceUri);
    }

    [Fact(Skip = ManualSkipMessage)]
    [Trait("Category", "Live")]
    public async Task DeltaSync_AfterAdd_ReturnsChangedResource()
    {
        var client = new CalDavClient();
        var settings = BuildConnectionSettings();
        var calendar = await GetTargetCalendarAsync(client, settings);

        var initialSyncToken = await GetCalendarSyncTokenAsync(settings, new Uri(calendar.RemoteCalendarId));
        initialSyncToken.Should().NotBeNullOrWhiteSpace();

        var eventId = $"wino-live-delta-{Guid.NewGuid():N}";
        var resourceUri = BuildEventResourceUri(calendar, eventId);

        await PutEventAsync(settings, resourceUri, BuildIcs(eventId, "Wino Live Delta", new DateTimeOffset(2026, 06, 01, 12, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 06, 01, 13, 0, 0, TimeSpan.Zero)));

        var deltaResponse = await ReportSyncCollectionAsync(settings, new Uri(calendar.RemoteCalendarId), initialSyncToken);
        var changedHrefs = ExtractChangedHrefs(deltaResponse);

        changedHrefs.Should().Contain(h => h.Contains($"{eventId}.ics", StringComparison.OrdinalIgnoreCase));

        await DeleteEventAsync(settings, resourceUri);
    }

    private static CalDavConnectionSettings BuildConnectionSettings()
        => new()
        {
            ServiceUri = new Uri(ServiceUri),
            Username = Username,
            Password = Password
        };

    private static async Task<CalDavCalendar> GetTargetCalendarAsync(CalDavClient client, CalDavConnectionSettings settings)
    {
        var calendars = await client.DiscoverCalendarsAsync(settings);
        calendars.Should().NotBeEmpty();
        return calendars.First();
    }

    private static Uri BuildEventResourceUri(CalDavCalendar calendar, string eventId)
        => new($"{calendar.RemoteCalendarId.TrimEnd('/')}/{eventId}.ics");

    private static string BuildIcs(string uid, string summary, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        return $"""
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Wino Mail//CalDAV Live Tests//EN
            CALSCALE:GREGORIAN
            BEGIN:VEVENT
            UID:{uid}
            DTSTAMP:{DateTimeOffset.UtcNow:yyyyMMdd'T'HHmmss'Z'}
            DTSTART:{startUtc:yyyyMMdd'T'HHmmss'Z'}
            DTEND:{endUtc:yyyyMMdd'T'HHmmss'Z'}
            SUMMARY:{summary}
            END:VEVENT
            END:VCALENDAR
            """;
    }

    private static async Task PutEventAsync(CalDavConnectionSettings settings, Uri eventUri, string icsContent)
    {
        using var client = CreateAuthenticatedHttpClient(settings);
        using var request = new HttpRequestMessage(HttpMethod.Put, eventUri)
        {
            Content = new StringContent(icsContent, Encoding.UTF8, "text/calendar")
        };

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private static async Task DeleteEventAsync(CalDavConnectionSettings settings, Uri eventUri)
    {
        using var client = CreateAuthenticatedHttpClient(settings);
        using var response = await client.DeleteAsync(eventUri);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> GetCalendarSyncTokenAsync(CalDavConnectionSettings settings, Uri calendarUri)
    {
        const string body = """
            <D:propfind xmlns:D="DAV:">
              <D:prop>
                <D:sync-token />
              </D:prop>
            </D:propfind>
            """;

        using var client = CreateAuthenticatedHttpClient(settings);
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), calendarUri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml")
        };

        request.Headers.Add("Depth", "0");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var xml = await response.Content.ReadAsStringAsync();
        var doc = XDocument.Parse(xml);

        return doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "sync-token")?.Value ?? string.Empty;
    }

    private static async Task<XDocument> ReportSyncCollectionAsync(CalDavConnectionSettings settings, Uri calendarUri, string syncToken)
    {
        var body = $"""
            <D:sync-collection xmlns:D="DAV:">
              <D:sync-token>{SecurityElement.Escape(syncToken)}</D:sync-token>
              <D:sync-level>1</D:sync-level>
              <D:prop>
                <D:getetag />
              </D:prop>
            </D:sync-collection>
            """;

        using var client = CreateAuthenticatedHttpClient(settings);
        using var request = new HttpRequestMessage(new HttpMethod("REPORT"), calendarUri)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml")
        };

        request.Headers.Add("Depth", "1");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var xml = await response.Content.ReadAsStringAsync();
        return XDocument.Parse(xml);
    }

    private static IReadOnlyList<string> ExtractChangedHrefs(XDocument deltaXml)
        => deltaXml
            .Descendants()
            .Where(x => x.Name.LocalName == "href")
            .Select(x => x.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

    private static HttpClient CreateAuthenticatedHttpClient(CalDavConnectionSettings settings)
    {
        var client = new HttpClient();
        var basicAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Password}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);
        return client;
    }
}
