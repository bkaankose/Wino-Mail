using System.Net;
using System.Net.Http;
using FluentAssertions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.CardDav;
using Wino.Services;
using Xunit;

namespace Wino.Core.Tests.Synchronizers;

public sealed class CalDavClientProtocolTests
{
    private static readonly CalDavConnectionSettings Settings = new()
    {
        ServiceUri = new Uri("https://dav.example.test/entry"),
        Username = "user",
        Password = "secret"
    };

    private static readonly CalDavCalendar Calendar = new()
    {
        RemoteCalendarId = "https://dav.example.test/calendars/work"
    };

    [Fact]
    public async Task UpsertCalendarEventAsync_Update_UsesExactHrefAndIfMatch()
    {
        var transport = new RecordingTransport(Response(HttpStatusCode.NoContent, eTag: "\"new\""));
        var sut = new CalDavClient(transport);

        var result = await sut.UpsertCalendarEventAsync(Settings, Calendar, new CalDavWriteRequest
        {
            RemoteEventId = "uid-not-the-filename",
            ExactHref = "https://dav.example.test/calendars/work/server-name.ics",
            ETag = "\"old\"",
            IcsContent = "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n"
        });

        var request = transport.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Put);
        request.Uri.Should().Be(new Uri("https://dav.example.test/calendars/work/server-name.ics"));
        request.Headers["If-Match"].Should().ContainSingle("\"old\"");
        request.Headers.Should().NotContainKey("If-None-Match");
        result.ExactHref.Should().Be(request.Uri.ToString());
        result.ETag.Should().Be("\"new\"");
        result.RequiresRefetch.Should().BeFalse();
    }

    [Fact]
    public async Task UpsertCalendarEventAsync_Create_UsesIfNoneMatchStar()
    {
        var transport = new RecordingTransport(Response(HttpStatusCode.Created));
        var sut = new CalDavClient(transport);

        var result = await sut.UpsertCalendarEventAsync(Settings, Calendar, new CalDavWriteRequest
        {
            RemoteEventId = "new uid",
            IcsContent = "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n",
            CreateOnly = true
        });

        var request = transport.Requests.Should().ContainSingle().Subject;
        request.Uri.AbsolutePath.Should().EndWith("/new%20uid.ics");
        request.Headers["If-None-Match"].Should().ContainSingle("*");
        request.Headers.Should().NotContainKey("If-Match");
        result.RequiresRefetch.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteCalendarEventAsync_UsesExactHrefAndIfMatch()
    {
        var transport = new RecordingTransport(Response(HttpStatusCode.NoContent));
        var sut = new CalDavClient(transport);

        await sut.DeleteCalendarEventAsync(
            Settings,
            "https://dav.example.test/calendars/work/server-name.ics",
            "W/\"etag\"");

        var request = transport.Requests.Should().ContainSingle().Subject;
        request.Method.Should().Be(HttpMethod.Delete);
        request.Uri.AbsolutePath.Should().EndWith("/server-name.ics");
        request.Headers["If-Match"].Should().ContainSingle("W/\"etag\"");
    }

    [Fact]
    public async Task DiscoverCalendarsAsync_EnumeratesEveryCalendarHomeSet()
    {
        var transport = new RecordingTransport(
            XmlResponse("""
                <D:multistatus xmlns:D="DAV:"><D:response><D:href>/entry</D:href><D:propstat><D:prop><D:current-user-principal><D:href>/principals/me/</D:href></D:current-user-principal></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response></D:multistatus>
                """),
            XmlResponse("""
                <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav"><D:response><D:href>/principals/me/</D:href><D:propstat><D:prop><C:calendar-home-set><D:href>/homes/one/</D:href><D:href>/homes/two/</D:href></C:calendar-home-set></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response></D:multistatus>
                """),
            CalendarCollectionResponse("work/", "Work"),
            CalendarCollectionResponse("family/", "Family"));
        var sut = new CalDavClient(transport);

        var calendars = await sut.DiscoverCalendarsAsync(Settings);

        calendars.Select(value => value.RemoteCalendarId).Should().BeEquivalentTo(
            "https://dav.example.test/homes/one/work",
            "https://dav.example.test/homes/two/family");
        transport.Requests.Select(value => value.Uri.AbsolutePath).Should().ContainInOrder(
            "/entry", "/principals/me/", "/homes/one/", "/homes/two/");
    }

    [Fact]
    public async Task DiscoverCalendarsAsync_Forbidden_MapsDavPermissionError()
    {
        var transport = new RecordingTransport(new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("<D:error xmlns:D=\"DAV:\"><D:need-privileges /></D:error>")
        });
        var sut = new CalDavClient(transport);

        var action = () => sut.DiscoverCalendarsAsync(Settings);

        var exception = await action.Should().ThrowAsync<DavRequestException>();
        exception.Which.StatusCode.Should().Be(403);
        exception.Which.HasError("need-privileges").Should().BeTrue();
    }

    private static HttpResponseMessage CalendarCollectionResponse(string href, string name)
        => XmlResponse($"""
            <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:response><D:href>{href}</D:href><D:propstat><D:prop>
                <D:resourcetype><D:collection/><C:calendar/></D:resourcetype>
                <D:displayname>{name}</D:displayname>
                <C:supported-calendar-component-set><C:comp name="VEVENT"/></C:supported-calendar-component-set>
              </D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
            </D:multistatus>
            """);

    private static HttpResponseMessage XmlResponse(string xml)
        => new(HttpStatusCode.MultiStatus) { Content = new StringContent(xml) };

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string eTag = null)
    {
        var response = new HttpResponseMessage(statusCode);
        if (eTag != null)
            response.Headers.TryAddWithoutValidation("ETag", eTag);
        return response;
    }

    private sealed class RecordingTransport(params HttpResponseMessage[] responses) : IDavTransport
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<RecordedRequest> Requests { get; } = [];

        public async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            DavAuthenticationProfile authentication,
            CancellationToken cancellationToken = default)
        {
            var headers = request.Headers
                .ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!,
                headers,
                request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));

            var response = _responses.Dequeue();
            response.RequestMessage = request;
            return response;
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        IReadOnlyDictionary<string, string[]> Headers,
        string Content);
}
