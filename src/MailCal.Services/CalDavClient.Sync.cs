#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Models.Calendar;
using MailCal.Core.Domain.Models.CardDav;

namespace MailCal.Services;

/// <summary>
/// Discovery with server capabilities, and the requests behind incremental synchronization:
/// sync-collection (RFC 6578), an ETag-only calendar-query, calendar-multiget and GET.
/// </summary>
public sealed partial class CalDavClient
{
    private const string CalendarCollectionProperties = """
                <D:resourcetype />
                <D:displayname />
                <D:owner />
                <D:current-user-privilege-set />
                <D:supported-report-set />
                <D:quota-available-bytes />
                <D:quota-used-bytes />
                <CS:getctag />
                <D:sync-token />
                <C:calendar-description />
                <C:calendar-timezone />
                <C:supported-calendar-component-set />
                <C:schedule-calendar-transp />
                <C:max-resource-size />
                <C:max-attendees-per-instance />
                <C:default-alarm-vevent-datetime />
                <C:default-alarm-vevent-date />
                <CS:default-alarm-vevent-datetime />
                <CS:default-alarm-vevent-date />
                <ICAL:calendar-color />
                <ICAL:calendar-order />
        """;

    private static readonly string CalendarCollectionPropFindBody = $"""
        <D:propfind xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav" xmlns:CS="http://calendarserver.org/ns/" xmlns:ICAL="http://apple.com/ns/ical/">
          <D:prop>
        {CalendarCollectionProperties}
          </D:prop>
        </D:propfind>
        """;

    public async Task<CalDavAccountDiscovery> DiscoverAccountAsync(
        CalDavConnectionSettings connectionSettings,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionSettings(connectionSettings);

        var principalUri = await DiscoverPrincipalUriAsync(connectionSettings, cancellationToken).ConfigureAwait(false);

        const string principalBody = """
            <D:propfind xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:prop>
                <C:calendar-home-set />
                <C:calendar-user-address-set />
                <C:schedule-inbox-URL />
                <C:schedule-outbox-URL />
              </D:prop>
            </D:propfind>
            """;

        var principalXml = await SendXmlAsync(
            connectionSettings,
            PropFindMethod,
            principalUri,
            depth: "0",
            principalBody,
            cancellationToken).ConfigureAwait(false);

        var principalProps = GetSuccessProps(principalXml).ToList();
        var homeUris = principalProps
            .SelectMany(prop => prop.Elements(CalDavNamespace + "calendar-home-set"))
            .SelectMany(homeSet => homeSet.Elements(DavNamespace + "href"))
            .Select(href => href.Value)
            .Where(href => !string.IsNullOrWhiteSpace(href))
            .Select(href => CreateAbsoluteUri(principalUri, href.Trim()))
            .DistinctBy(uri => uri.AbsoluteUri, StringComparer.Ordinal)
            .ToList();

        if (homeUris.Count == 0)
            homeUris.Add(principalUri);

        var addresses = principalProps
            .SelectMany(prop => prop.Elements(CalDavNamespace + "calendar-user-address-set"))
            .SelectMany(set => set.Elements(DavNamespace + "href"))
            .Select(href => href.Value.Trim())
            .Where(IsUsableCalendarUserAddress)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var inboxHref = ReadHrefProperty(principalProps, CalDavNamespace + "schedule-inbox-URL", principalUri);
        var outboxHref = ReadHrefProperty(principalProps, CalDavNamespace + "schedule-outbox-URL", principalUri);
        var davCapabilities = await ReadDavCapabilitiesAsync(connectionSettings, homeUris[0], cancellationToken).ConfigureAwait(false);

        var calendars = new List<CalDavCalendar>();
        var homeDefaultAlarmDateTime = string.Empty;
        var homeDefaultAlarmDate = string.Empty;

        foreach (var homeUri in homeUris)
        {
            var listing = await SendXmlAsync(
                connectionSettings,
                PropFindMethod,
                homeUri,
                depth: "1",
                CalendarCollectionPropFindBody,
                cancellationToken).ConfigureAwait(false);

            calendars.AddRange(ParseCalendarCollections(listing, homeUri, includeNonEventCalendars: true));

            var homeProp = FindCollectionProp(listing, homeUri);
            if (homeProp != null && string.IsNullOrEmpty(homeDefaultAlarmDateTime))
            {
                homeDefaultAlarmDateTime = ReadDefaultAlarm(homeProp, "default-alarm-vevent-datetime");
                homeDefaultAlarmDate = ReadDefaultAlarm(homeProp, "default-alarm-vevent-date");
            }
        }

        return new CalDavAccountDiscovery
        {
            PrincipalHref = principalUri.AbsoluteUri,
            CalendarHomeHrefs = homeUris.Select(uri => uri.AbsoluteUri).ToArray(),
            CalendarUserAddresses = addresses.ToArray(),
            ScheduleInboxHref = inboxHref,
            ScheduleOutboxHref = outboxHref,
            DavCapabilities = davCapabilities,
            DefaultAlarmVEventDateTime = homeDefaultAlarmDateTime,
            DefaultAlarmVEventDate = homeDefaultAlarmDate,
            Calendars = DistinctCalendars(calendars)
        };
    }

    public async Task<IReadOnlyList<CalDavCalendar>> ListCalendarsAsync(
        CalDavConnectionSettings connectionSettings,
        IReadOnlyList<string> calendarHomeHrefs,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionSettings(connectionSettings);
        ArgumentNullException.ThrowIfNull(calendarHomeHrefs);

        var calendars = new List<CalDavCalendar>();
        foreach (var homeHref in calendarHomeHrefs.Where(href => !string.IsNullOrWhiteSpace(href)))
        {
            var homeUri = new Uri(homeHref, UriKind.Absolute);
            var listing = await SendXmlAsync(
                connectionSettings,
                PropFindMethod,
                homeUri,
                depth: "1",
                CalendarCollectionPropFindBody,
                cancellationToken).ConfigureAwait(false);

            calendars.AddRange(ParseCalendarCollections(listing, homeUri, includeNonEventCalendars: true));
        }

        return DistinctCalendars(calendars);
    }

    public async Task<CalDavSyncPage> SyncCollectionAsync(
        CalDavConnectionSettings connectionSettings,
        CalDavCalendar calendar,
        string? syncToken,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionSettings(connectionSettings);
        var collectionUri = GetCollectionUri(calendar);
        var tokenXml = string.IsNullOrEmpty(syncToken)
            ? "<D:sync-token />"
            : $"<D:sync-token>{SecurityElement.Escape(syncToken)}</D:sync-token>";

        var body = $"""
            <D:sync-collection xmlns:D="DAV:">
              {tokenXml}
              <D:sync-level>1</D:sync-level>
              <D:prop>
                <D:getetag />
              </D:prop>
            </D:sync-collection>
            """;

        // RFC 6578 section 3.2: Depth 0; the sync-level element scopes the report.
        var responseXml = await SendXmlAsync(
            connectionSettings,
            ReportMethod,
            collectionUri,
            depth: "0",
            body,
            cancellationToken).ConfigureAwait(false);

        var isTruncated = false;
        var changes = new List<CalDavResource>();

        foreach (var response in TopLevelResponses(responseXml))
        {
            var href = response.Element(DavNamespace + "href")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(href))
                continue;

            var resourceUri = CreateAbsoluteUri(collectionUri, href);
            var status = ParseStatusCode(response.Element(DavNamespace + "status")?.Value);

            if (IsSameResource(resourceUri, collectionUri))
            {
                // The collection itself only appears to say the listing was cut short
                // (RFC 6578 section 3.6) or that the whole request failed.
                var propStatuses = response.Elements(DavNamespace + "propstat")
                    .Select(propstat => ParseStatusCode(propstat.Element(DavNamespace + "status")?.Value));

                if (status == 507 || propStatuses.Any(value => value == 507))
                {
                    isTruncated = true;
                    continue;
                }

                if (status is >= 400)
                    throw new DavRequestException(status.Value, Translator.DavError_InvalidResponse, ReadErrorNames(response));

                continue;
            }

            if (resourceUri.AbsolutePath.EndsWith('/'))
                continue;

            changes.Add(new CalDavResource
            {
                ExactHref = resourceUri.AbsoluteUri,
                ETag = ReadETag(response),
                IsDeleted = status == 404
            });
        }

        return new CalDavSyncPage
        {
            Changes = changes,
            NextSyncToken = responseXml.Root?.Element(DavNamespace + "sync-token")?.Value?.Trim() ?? string.Empty,
            IsTruncated = isTruncated
        };
    }

    public async Task<IReadOnlyList<CalDavResource>> ListEventResourcesAsync(
        CalDavConnectionSettings connectionSettings,
        CalDavCalendar calendar,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionSettings(connectionSettings);
        var collectionUri = GetCollectionUri(calendar);
        var startString = startUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var endString = endUtc.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        var body = $"""
            <C:calendar-query xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:prop>
                <D:getetag />
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
            collectionUri,
            depth: "1",
            body,
            cancellationToken).ConfigureAwait(false);

        var resources = new List<CalDavResource>();
        foreach (var response in TopLevelResponses(responseXml))
        {
            var href = response.Element(DavNamespace + "href")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(href))
                continue;

            var resourceUri = CreateAbsoluteUri(collectionUri, href);
            var status = ParseStatusCode(response.Element(DavNamespace + "status")?.Value);
            if (IsSameResource(resourceUri, collectionUri) || resourceUri.AbsolutePath.EndsWith('/') || status is >= 300)
                continue;

            resources.Add(new CalDavResource
            {
                ExactHref = resourceUri.AbsoluteUri,
                ETag = ReadETag(response)
            });
        }

        return resources
            .DistinctBy(resource => ResourceKey(new Uri(resource.ExactHref)), StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<CalDavResource>> MultiGetEventsAsync(
        CalDavConnectionSettings connectionSettings,
        CalDavCalendar calendar,
        IReadOnlyList<string> hrefs,
        CancellationToken cancellationToken = default)
    {
        if (hrefs == null || hrefs.Count == 0)
            return Array.Empty<CalDavResource>();

        ValidateConnectionSettings(connectionSettings);
        var collectionUri = GetCollectionUri(calendar);

        // Servers answer with their own spelling of an href (relative, re-encoded). The caller
        // tracks resources by the href it asked for, so answers are mapped back.
        var requested = new Dictionary<string, string>(StringComparer.Ordinal);
        var hrefXml = new StringBuilder();
        foreach (var href in hrefs)
        {
            var resourceUri = CreateAbsoluteUri(collectionUri, href);
            requested[ResourceKey(resourceUri)] = href;

            // RFC 4918 absolute-path form for resources on this server. iCloud rejects
            // absolute URLs inside multiget even though it returns them in REPORTs.
            var sameOrigin = string.Equals(
                resourceUri.GetLeftPart(UriPartial.Authority),
                collectionUri.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase);

            hrefXml.Append("<D:href>")
                .Append(SecurityElement.Escape(sameOrigin ? resourceUri.PathAndQuery : resourceUri.AbsoluteUri))
                .Append("</D:href>");
        }

        var body = $"""
            <C:calendar-multiget xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:prop>
                <D:getetag />
                <C:calendar-data />
              </D:prop>
              {hrefXml}
            </C:calendar-multiget>
            """;

        // The hrefs, not Depth, scope a multiget (RFC 4791 section 7.9).
        var responseXml = await SendXmlAsync(
            connectionSettings,
            ReportMethod,
            collectionUri,
            depth: "1",
            body,
            cancellationToken).ConfigureAwait(false);

        var resources = new List<CalDavResource>();
        foreach (var response in TopLevelResponses(responseXml))
        {
            var href = response.Element(DavNamespace + "href")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(href) ||
                !requested.TryGetValue(ResourceKey(CreateAbsoluteUri(collectionUri, href)), out var requestedHref))
                continue;

            var status = ParseStatusCode(response.Element(DavNamespace + "status")?.Value);
            if (status == 404)
            {
                resources.Add(new CalDavResource { ExactHref = requestedHref, IsDeleted = true });
                continue;
            }

            var calendarData = GetSuccessProps(response)
                .Select(prop => prop.Element(CalDavNamespace + "calendar-data")?.Value)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            if (string.IsNullOrWhiteSpace(calendarData))
                continue;

            resources.Add(new CalDavResource
            {
                ExactHref = requestedHref,
                ETag = ReadETag(response),
                IcsContent = calendarData
            });
        }

        return resources;
    }

    public async Task<CalDavResource> GetEventResourceAsync(
        CalDavConnectionSettings connectionSettings,
        string exactHref,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionSettings(connectionSettings);

        if (string.IsNullOrWhiteSpace(exactHref))
            throw new ArgumentException("The exact CalDAV resource href is required.", nameof(exactHref));

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(exactHref, UriKind.Absolute));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/calendar"));

        using var response = await _davTransport.SendAsync(
            request,
            CreateAuthentication(connectionSettings),
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            return new CalDavResource { ExactHref = exactHref, IsDeleted = true };

        await _responseHandler.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        var content = response.Content == null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return new CalDavResource
        {
            ExactHref = exactHref,
            ETag = response.Headers.ETag?.ToString() ?? string.Empty,
            IcsContent = content
        };
    }

    public IReadOnlyList<CalDavCalendarEvent> ExpandCalendarResource(
        string icsContent,
        string resourceHref,
        string eTag,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc)
    {
        if (string.IsNullOrWhiteSpace(icsContent))
            return Array.Empty<CalDavCalendarEvent>();

        // Recurring parents are saved before their occurrences and exceptions.
        return ParseCalendarData(icsContent, resourceHref ?? string.Empty, eTag ?? string.Empty, startUtc, endUtc)
            .OrderByDescending(calendarEvent => calendarEvent.IsSeriesMaster)
            .ThenBy(calendarEvent => calendarEvent.Start)
            .ToList();
    }

    /// <summary>
    /// Reads the DAV compliance classes (RFC 4918 section 10.1) from an OPTIONS response. Servers
    /// such as Yahoo and Zimbra return none; a failure here never fails discovery.
    /// </summary>
    private async Task<IReadOnlyList<string>> ReadDavCapabilitiesAsync(
        CalDavConnectionSettings connectionSettings,
        Uri uri,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, uri);
            using var response = await _davTransport.SendAsync(
                request,
                CreateAuthentication(connectionSettings),
                cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode || !response.Headers.TryGetValues("DAV", out var values))
                return Array.Empty<string>();

            return values
                .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Information(ex, "CalDAV OPTIONS on {Path} failed; continuing without DAV capabilities", uri.AbsolutePath);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Every calendar collection in a Depth 1 listing. With <paramref name="includeNonEventCalendars"/>
    /// false, collections whose component set lacks VEVENT (task lists) are left out.
    /// </summary>
    private static List<CalDavCalendar> ParseCalendarCollections(XDocument xml, Uri baseUri, bool includeNonEventCalendars)
    {
        var result = new List<CalDavCalendar>();

        foreach (var response in xml.Descendants(DavNamespace + "response"))
        {
            var href = response.Element(DavNamespace + "href")?.Value;
            if (string.IsNullOrWhiteSpace(href))
                continue;

            // A server may split successful properties over several propstat elements.
            var prop = new XElement(DavNamespace + "prop", GetSuccessProps(response).SelectMany(value => value.Elements()));
            var resourceType = prop.Element(DavNamespace + "resourcetype");
            if (resourceType == null || !resourceType.Elements(CalDavNamespace + "calendar").Any())
                continue;

            var supportedComponents = prop.Elements(CalDavNamespace + "supported-calendar-component-set")
                .Elements(CalDavNamespace + "comp")
                .Select(e => e.Attribute("name")?.Value?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.ToUpperInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var supportsEvents = supportedComponents.Length == 0 ||
                                 supportedComponents.Contains("VEVENT", StringComparer.OrdinalIgnoreCase);

            if (!supportsEvents && !includeNonEventCalendars)
                continue;

            var remoteUri = CreateAbsoluteUri(EnsureCollectionUri(baseUri), href.Trim()).ToString().TrimEnd('/');
            var displayName = prop.Element(DavNamespace + "displayname")?.Value ?? string.Empty;
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = WebUtility.UrlDecode(remoteUri.Split('/').LastOrDefault() ?? "Calendar");

            var syncToken = prop.Element(DavNamespace + "sync-token")?.Value?.Trim() ?? string.Empty;
            var reports = prop.Elements(DavNamespace + "supported-report-set")
                .Elements(DavNamespace + "supported-report")
                .Elements(DavNamespace + "report")
                .SelectMany(report => report.Elements())
                .Select(report => $"{{{report.Name.NamespaceName}}}{report.Name.LocalName}")
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            // Without a report set the server did not say; a sync token on the collection
            // implies sync-collection (RFC 6578 section 4), and multiget is required by RFC 4791.
            var supportsSyncCollection = reports.Length > 0
                ? reports.Contains($"{{{DavNamespace.NamespaceName}}}sync-collection", StringComparer.Ordinal)
                : !string.IsNullOrEmpty(syncToken);
            var supportsMultiget = reports.Length == 0 ||
                                   reports.Contains($"{{{CalDavNamespace.NamespaceName}}}calendar-multiget", StringComparer.Ordinal);

            result.Add(new CalDavCalendar
            {
                RemoteCalendarId = remoteUri,
                Name = displayName,
                Description = prop.Element(CalDavNamespace + "calendar-description")?.Value ?? string.Empty,
                CTag = prop.Element(CalendarServerNamespace + "getctag")?.Value ?? string.Empty,
                SyncToken = syncToken,
                TimeZone = ExtractCalendarTimeZoneId(prop.Element(CalDavNamespace + "calendar-timezone")?.Value ?? string.Empty),
                BackgroundColorHex = NormalizeCalendarColor(prop.Element(AppleCalendarNamespace + "calendar-color")?.Value ?? string.Empty),
                IsReadOnly = IsCalendarReadOnly(prop),
                SupportsEvents = supportsEvents,
                DefaultShowAs = GetDefaultShowAs(prop),
                Order = ParseCalendarOrder(prop.Element(AppleCalendarNamespace + "calendar-order")?.Value ?? string.Empty),
                SupportedComponents = supportedComponents,
                SupportedReports = reports,
                SupportsSyncCollection = supportsSyncCollection,
                SupportsMultiget = supportsMultiget,
                QuotaAvailableBytes = ParseLong(prop.Element(DavNamespace + "quota-available-bytes")?.Value),
                QuotaUsedBytes = ParseLong(prop.Element(DavNamespace + "quota-used-bytes")?.Value),
                MaxResourceSize = ParseLong(prop.Element(CalDavNamespace + "max-resource-size")?.Value),
                MaxAttendeesPerInstance = ParseInt(prop.Element(CalDavNamespace + "max-attendees-per-instance")?.Value),
                DefaultAlarmVEventDateTime = ReadDefaultAlarm(prop, "default-alarm-vevent-datetime"),
                DefaultAlarmVEventDate = ReadDefaultAlarm(prop, "default-alarm-vevent-date"),
                OwnerHref = prop.Element(DavNamespace + "owner")?.Element(DavNamespace + "href")?.Value?.Trim() ?? string.Empty
            });
        }

        return result;
    }

    private static IEnumerable<XElement> TopLevelResponses(XDocument xml)
        => xml.Root?.Elements(DavNamespace + "response") ?? Enumerable.Empty<XElement>();

    private static List<CalDavCalendar> DistinctCalendars(IEnumerable<CalDavCalendar> calendars)
        => calendars
            .GroupBy(calendar => calendar.RemoteCalendarId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

    private static XElement? FindCollectionProp(XDocument xml, Uri collectionUri)
    {
        foreach (var response in xml.Descendants(DavNamespace + "response"))
        {
            var href = response.Element(DavNamespace + "href")?.Value;
            if (string.IsNullOrWhiteSpace(href) || !IsSameResource(CreateAbsoluteUri(collectionUri, href.Trim()), collectionUri))
                continue;

            return new XElement(DavNamespace + "prop", GetSuccessProps(response).SelectMany(value => value.Elements()));
        }

        return null;
    }

    /// <summary>The draft names the property in the CalDAV namespace; CalendarServer-style servers may use their own.</summary>
    private static string ReadDefaultAlarm(XElement prop, string localName)
    {
        var value = prop.Element(CalDavNamespace + localName)?.Value ??
                    prop.Element(CalendarServerNamespace + localName)?.Value;

        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static string ReadHrefProperty(IEnumerable<XElement> props, XName name, Uri baseUri)
    {
        var href = props
            .Select(prop => prop.Element(name)?.Element(DavNamespace + "href")?.Value?.Trim())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        return string.IsNullOrWhiteSpace(href) ? string.Empty : CreateAbsoluteUri(baseUri, href).AbsoluteUri;
    }

    /// <summary>
    /// Keeps every address a server lists except mailto values without an address. Yahoo
    /// reports "mailto:null", which must never become an organizer or attendee.
    /// </summary>
    private static bool IsUsableCalendarUserAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (!value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return true;

        var address = value[7..].Trim();
        return address.Contains('@') && !string.Equals(address, "null", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadETag(XElement response)
        => GetSuccessProps(response)
               .Select(prop => prop.Element(DavNamespace + "getetag")?.Value?.Trim())
               .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
           ?? string.Empty;

    private static string[] ReadErrorNames(XElement response)
        => response.Element(DavNamespace + "error")?.Elements()
               .Select(element => $"{{{element.Name.NamespaceName}}}{element.Name.LocalName}")
               .ToArray()
           ?? [];

    private static int? ParseStatusCode(string? statusLine)
    {
        if (string.IsNullOrWhiteSpace(statusLine))
            return null;

        var parts = statusLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) ? code : null;
    }

    private static long? ParseLong(string? value)
        => long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static int? ParseInt(string? value)
        => int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static Uri GetCollectionUri(CalDavCalendar calendar)
    {
        if (calendar == null || string.IsNullOrWhiteSpace(calendar.RemoteCalendarId))
            throw new ArgumentException("Calendar remote ID is required.");

        return EnsureCollectionUri(new Uri(calendar.RemoteCalendarId, UriKind.Absolute));
    }

    /// <summary>
    /// Identity of a resource regardless of how a server spells its href: percent-encoding and a
    /// collection's trailing slash both vary.
    /// </summary>
    internal static string ResourceKey(Uri uri) => Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');

    private static bool IsSameResource(Uri left, Uri right)
        => string.Equals(ResourceKey(left), ResourceKey(right), StringComparison.Ordinal);
}
