#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Wino.Core.Domain;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Services;

/// <summary>VTODO listings and calendar collection management (MKCALENDAR, PROPPATCH, DELETE).</summary>
public sealed partial class CalDavClient
{
    private static readonly HttpMethod MkCalendarMethod = new("MKCALENDAR");
    private static readonly HttpMethod PropPatchMethod = new("PROPPATCH");

    public async Task<IReadOnlyList<CalDavResource>> ListTaskResourcesAsync(
        CalDavConnectionSettings connectionSettings,
        CalDavCalendar calendar,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionSettings(connectionSettings);
        var collectionUri = GetCollectionUri(calendar);

        const string body = """
            <C:calendar-query xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:prop>
                <D:getetag />
              </D:prop>
              <C:filter>
                <C:comp-filter name="VCALENDAR">
                  <C:comp-filter name="VTODO" />
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

    public async Task<string> CreateCalendarCollectionAsync(
        CalDavConnectionSettings connectionSettings,
        string calendarHomeHref,
        string collectionName,
        string displayName,
        IReadOnlyList<string> components,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionSettings(connectionSettings);

        if (string.IsNullOrWhiteSpace(calendarHomeHref))
            throw new ArgumentException("The calendar home is required to create a collection.", nameof(calendarHomeHref));

        if (string.IsNullOrWhiteSpace(collectionName))
            throw new ArgumentException("The collection name is required.", nameof(collectionName));

        var homeUri = EnsureCollectionUri(new Uri(calendarHomeHref, UriKind.Absolute));
        var collectionUri = new Uri(homeUri, $"{Uri.EscapeDataString(collectionName.Trim())}/");

        var componentXml = new StringBuilder();
        foreach (var component in components ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(component))
                componentXml.Append("<C:comp name=\"").Append(SecurityElement.Escape(component.Trim().ToUpperInvariant())).Append("\" />");
        }

        var componentSet = componentXml.Length == 0
            ? string.Empty
            : $"<C:supported-calendar-component-set>{componentXml}</C:supported-calendar-component-set>";

        var body = $"""
            <C:mkcalendar xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:set>
                <D:prop>
                  <D:displayname>{SecurityElement.Escape(displayName ?? string.Empty)}</D:displayname>
                  {componentSet}
                </D:prop>
              </D:set>
            </C:mkcalendar>
            """;

        using var request = new HttpRequestMessage(MkCalendarMethod, collectionUri)
        {
            Content = new StringContent(XmlDeclaration + body, Encoding.UTF8, "application/xml")
        };

        try
        {
            using var response = await SendAsync(connectionSettings, request, cancellationToken).ConfigureAwait(false);
        }
        catch (DavRequestException ex) when (ex.StatusCode is 403 or 405 or 501)
        {
            throw new NotSupportedException(Translator.CalDavTasks_ListManagementUnsupported, ex);
        }

        return collectionUri.ToString().TrimEnd('/');
    }

    public async Task UpdateCalendarCollectionAsync(
        CalDavConnectionSettings connectionSettings,
        string remoteCalendarId,
        string displayName,
        string? colorHex,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionSettings(connectionSettings);

        if (string.IsNullOrWhiteSpace(remoteCalendarId))
            throw new ArgumentException("Calendar remote ID is required.", nameof(remoteCalendarId));

        var collectionUri = EnsureCollectionUri(new Uri(remoteCalendarId, UriKind.Absolute));

        await PropPatchAsync(
            connectionSettings,
            collectionUri,
            $"<D:displayname>{SecurityElement.Escape(displayName ?? string.Empty)}</D:displayname>",
            cancellationToken).ConfigureAwait(false);

        var color = NormalizeOutgoingColor(colorHex);
        if (color == null)
            return;

        try
        {
            await PropPatchAsync(
                connectionSettings,
                collectionUri,
                $"<ICAL:calendar-color>{SecurityElement.Escape(color)}</ICAL:calendar-color>",
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is DavRequestException or HttpRequestException or XmlException)
        {
            // Apple's calendar-color is an extension; servers without it keep the default color.
            Logger.Information(ex, "CalDAV server refused calendar-color on {Path}", collectionUri.AbsolutePath);
        }
    }

    public async Task DeleteCalendarCollectionAsync(
        CalDavConnectionSettings connectionSettings,
        string remoteCalendarId,
        CancellationToken cancellationToken = default)
    {
        ValidateConnectionSettings(connectionSettings);

        if (string.IsNullOrWhiteSpace(remoteCalendarId))
            throw new ArgumentException("Calendar remote ID is required.", nameof(remoteCalendarId));

        var collectionUri = EnsureCollectionUri(new Uri(remoteCalendarId, UriKind.Absolute));
        using var request = new HttpRequestMessage(DeleteMethod, collectionUri);

        using var response = await _davTransport.SendAsync(
            request,
            CreateAuthentication(connectionSettings),
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            return;

        try
        {
            await _responseHandler.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (DavRequestException ex) when (ex.StatusCode is 403 or 405 or 501)
        {
            throw new NotSupportedException(Translator.CalDavTasks_ListManagementUnsupported, ex);
        }

        // RFC 4918 section 9.6.1: a 207 on DELETE lists the members that could not be removed.
        if (response.StatusCode == HttpStatusCode.MultiStatus && response.Content != null)
        {
            var xml = await ReadXmlAsync(response, cancellationToken).ConfigureAwait(false);
            var failure = TopLevelResponses(xml)
                .Select(item => ParseStatusCode(item.Element(DavNamespace + "status")?.Value))
                .FirstOrDefault(status => status is >= 400 and not 404);

            if (failure is int status)
            {
                if (status is 403 or 405)
                    throw new NotSupportedException(Translator.CalDavTasks_ListManagementUnsupported);

                throw new DavRequestException(status, Translator.DavError_InvalidResponse);
            }
        }
    }

    /// <summary>
    /// Sets one property. Servers answer 207 with a status per property (RFC 4918 section 9.2.1);
    /// a failed property is raised as that status.
    /// </summary>
    private async Task PropPatchAsync(
        CalDavConnectionSettings connectionSettings,
        Uri collectionUri,
        string propertyXml,
        CancellationToken cancellationToken)
    {
        var body = $"""
            <D:propertyupdate xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav" xmlns:ICAL="http://apple.com/ns/ical/">
              <D:set>
                <D:prop>
                  {propertyXml}
                </D:prop>
              </D:set>
            </D:propertyupdate>
            """;

        using var request = new HttpRequestMessage(PropPatchMethod, collectionUri)
        {
            Content = new StringContent(XmlDeclaration + body, Encoding.UTF8, "application/xml")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));

        using var response = await SendAsync(connectionSettings, request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.MultiStatus || response.Content == null)
            return;

        var xml = await ReadXmlAsync(response, cancellationToken).ConfigureAwait(false);
        var failure = xml.Descendants(DavNamespace + "propstat")
            .Select(propstat => ParseStatusCode(propstat.Element(DavNamespace + "status")?.Value))
            .FirstOrDefault(status => status is >= 300);

        if (failure is int failedStatus)
            throw new DavRequestException(failedStatus, Translator.DavError_InvalidResponse);
    }

    private static async Task<XDocument> ReadXmlAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(content))
            return new XDocument(new XElement("empty"));

        using var reader = XmlReader.Create(new System.IO.StringReader(content), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });

        return XDocument.Load(reader);
    }

    /// <summary>Apple clients write "#RRGGBBAA"; "#RRGGBB" is accepted by the servers that store the property.</summary>
    private static string? NormalizeOutgoingColor(string? colorHex)
    {
        if (string.IsNullOrWhiteSpace(colorHex))
            return null;

        var value = colorHex.Trim();
        if (!value.StartsWith('#'))
            value = "#" + value;

        return value.Length is 7 or 9 && value.Skip(1).All(Uri.IsHexDigit) ? value.ToUpperInvariant() : null;
    }
}
