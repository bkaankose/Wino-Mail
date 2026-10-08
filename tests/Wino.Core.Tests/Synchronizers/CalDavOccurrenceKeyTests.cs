using System.Net;
using System.Net.Http;
using System.Xml.Linq;
using FluentAssertions;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.CardDav;
using Wino.Services;
using Xunit;
using IcalCalendar = Ical.Net.Calendar;

namespace Wino.Core.Tests.Synchronizers;

/// <summary>
/// RFC 5545 requires RECURRENCE-ID to use the value type of DTSTART, but some clients
/// write a floating RECURRENCE-ID against a zoned series (or the reverse). The occurrence
/// key must still match the generated occurrence, otherwise the exception shows up twice.
/// </summary>
public sealed class CalDavOccurrenceKeyTests
{
    private const string ZonedSeriesWithFloatingException = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Wino Mail//Tests//EN
        BEGIN:VEVENT
        UID:zoned-series
        DTSTAMP:20260901T000000Z
        DTSTART;TZID=Europe/Berlin:20260914T100000
        DTEND;TZID=Europe/Berlin:20260914T110000
        RRULE:FREQ=DAILY;COUNT=5
        SUMMARY:Standup
        END:VEVENT
        BEGIN:VEVENT
        UID:zoned-series
        DTSTAMP:20260901T000000Z
        RECURRENCE-ID:20260916T100000
        DTSTART;TZID=Europe/Berlin:20260916T140000
        DTEND;TZID=Europe/Berlin:20260916T150000
        SUMMARY:Standup moved
        END:VEVENT
        END:VCALENDAR
        """;

    [Fact]
    public async Task ParseCalendarData_FloatingRecurrenceIdOnZonedSeries_ReplacesGeneratedOccurrence()
    {
        var client = new CalDavClient(new CalendarReportTransport(ZonedSeriesWithFloatingException));
        var remoteEvents = await client.GetCalendarEventsAsync(
            new CalDavConnectionSettings
            {
                ServiceUri = new Uri("https://dav.example.test/dav.php/"),
                Username = "user",
                Password = "test-password"
            },
            new CalDavCalendar { RemoteCalendarId = "https://dav.example.test/dav.php/calendars/user/default/" },
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

        remoteEvents.Should().HaveCount(6, "one master and five occurrences, the exception replaces one of them");
        remoteEvents.Select(value => value.RemoteEventId).Should().OnlyHaveUniqueItems();

        var movedRows = remoteEvents.Where(value => value.RemoteEventId.Contains("20260916", StringComparison.Ordinal)).ToList();
        movedRows.Should().ContainSingle();

        var moved = movedRows[0];
        moved.RemoteEventId.Should().Be("zoned-series::20260916T080000Z", "keys are expressed in the master's value type (UTC)");
        moved.Title.Should().Be("Standup moved");
        moved.Start.Should().Be(new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero));
        moved.SeriesMasterRemoteEventId.Should().Be("zoned-series");
        moved.IsRecurringInstance.Should().BeTrue();
    }

    [Fact]
    public void UpdateEvent_KeyInMasterValueType_FindsFloatingException()
    {
        var item = new CalendarItem
        {
            RemoteEventId = "zoned-series::20260916T080000Z",
            Title = "Standup moved again",
            StartDate = new DateTime(2026, 9, 16, 16, 0, 0),
            DurationInSeconds = 3600,
            StartTimeZone = "Europe/Berlin",
            EndTimeZone = "Europe/Berlin"
        };

        var result = CalDavIcsMutator.UpdateEvent(ZonedSeriesWithFloatingException, item, []);
        var calendar = IcalCalendar.Load(result);

        calendar.Events.Should().HaveCount(2, "the existing exception is updated instead of adding a second one");
        var exception = calendar.Events.Single(value => value.RecurrenceIdentifier != null);
        exception.Summary.Should().Be("Standup moved again");
    }

    [Fact]
    public void RemoveOccurrence_KeyInMasterValueType_RemovesFloatingException()
    {
        var result = CalDavIcsMutator.RemoveOccurrence(ZonedSeriesWithFloatingException, "zoned-series::20260916T080000Z");
        var calendar = IcalCalendar.Load(result);

        calendar.Events.Should().ContainSingle("the floating exception is removed instead of being left next to the EXDATE");
        result.Should().Contain("EXDATE");
        result.Should().Contain("20260916T080000Z");
    }

    private sealed class CalendarReportTransport(string ics) : IDavTransport
    {
        public Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            DavAuthenticationProfile authentication,
            CancellationToken cancellationToken = default)
        {
            request.Method.Method.Should().Be("REPORT");
            XNamespace dav = "DAV:";
            XNamespace calDav = "urn:ietf:params:xml:ns:caldav";
            var response = new XElement(dav + "multistatus",
                new XElement(dav + "response",
                    new XElement(dav + "href", "/dav.php/calendars/user/default/series.ics"),
                    new XElement(dav + "propstat",
                        new XElement(dav + "prop",
                            new XElement(dav + "getetag", "\"series-etag\""),
                            new XElement(calDav + "calendar-data", ics)),
                        new XElement(dav + "status", "HTTP/1.1 200 OK"))));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                RequestMessage = request,
                Content = new StringContent(response.ToString())
            });
        }
    }
}
