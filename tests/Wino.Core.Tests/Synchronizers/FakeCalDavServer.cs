using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Xml.Linq;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Core.Tests.Synchronizers;

/// <summary>
/// An in-memory CalDAV server with one event calendar and one task list, modeled on iCloud:
/// sync-collection with numbered tokens, calendar-multiget that only accepts absolute-path
/// hrefs, a DAV header on OPTIONS, and quota and attendee limits on the calendar. Switches turn
/// it into a Yahoo- or Zimbra-like server: no sync-collection, an empty multistatus for REPORTs
/// it does not implement, no multiget.
/// </summary>
internal sealed class FakeCalDavServer : IDavTransport
{
    private static readonly XNamespace Dav = "DAV:";
    private readonly Dictionary<string, (string Ics, int Revision)> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _deletedAt = new(StringComparer.Ordinal);
    private int _revision = 1;

    public const string Origin = "https://caldav.example.test";
    public const string PrincipalPath = "/123/principal/";
    public const string HomePath = "/123/calendars/";
    public const string CalendarPath = "/123/calendars/work/";
    public const string TasksPath = "/123/calendars/tasks/";
    public static string ServiceUrl => Origin + "/";
    public static string CalendarId => Origin + CalendarPath.TrimEnd('/');

    public List<string> Requests { get; } = [];
    public List<string> PreferHeaders { get; } = [];

    /// <summary>Number of resources the server returned with calendar data.</summary>
    public int CalendarDataResponses { get; private set; }

    public bool SupportsSyncCollection { get; set; } = true;

    /// <summary>Lists sync-collection in the report set even when it is not implemented.</summary>
    public bool AdvertiseSyncCollection { get; set; } = true;

    public bool SupportsMultiget { get; set; } = true;

    /// <summary>Yahoo and Zimbra answer a REPORT they do not implement with an empty 207.</summary>
    public bool EmptyMultistatusForUnsupportedReports { get; set; }

    public string DavHeader { get; set; } = "1, 3, access-control, calendar-access, calendar-schedule, calendar-auto-schedule, calendar-default-alarms, calendar-managed-attachments";

    /// <summary>Refuses the ETag-only calendar-query, leaving only the query with calendar data.</summary>
    public bool RefuseEtagQuery { get; set; }

    /// <summary>When positive, sync-collection returns at most this many changes and a 507 for the rest.</summary>
    public int SyncPageSize { get; set; }

    /// <summary>Tokens older than this revision are refused, as after server-side token expiry.</summary>
    public int OldestValidRevision { get; set; }

    public string HrefOf(string name) => Origin + CalendarPath + name + ".ics";
    public bool Has(string name) => _resources.ContainsKey(CalendarPath + name + ".ics");
    public string ETagOf(string name) => ETag(_resources[CalendarPath + name + ".ics"].Revision);
    public string IcsOf(string name) => _resources[CalendarPath + name + ".ics"].Ics;

    public void Put(string name, string ics)
    {
        var path = CalendarPath + name + ".ics";
        _resources[path] = (ics, ++_revision);
        _deletedAt.Remove(path);
    }

    public void Delete(string name)
    {
        var path = CalendarPath + name + ".ics";
        if (_resources.Remove(path)) _deletedAt[path] = ++_revision;
    }

    public void ClearLog()
    {
        Requests.Clear();
        PreferHeaders.Clear();
        CalendarDataResponses = 0;
    }

    public static string Event(string uid, string summary, DateTime startUtc, string? rrule = null)
        => "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Wino//FakeCalDav//EN\r\nBEGIN:VEVENT\r\n" +
           $"UID:{uid}\r\nDTSTAMP:20260101T000000Z\r\n" +
           $"DTSTART:{startUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}\r\n" +
           $"DTEND:{startUtc.AddHours(1).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}\r\n" +
           $"SUMMARY:{summary}\r\n" +
           (rrule is null ? "" : $"RRULE:{rrule}\r\n") +
           "END:VEVENT\r\nEND:VCALENDAR\r\n";

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, DavAuthenticationProfile authentication, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri!.AbsolutePath;
        var report = body.Contains("sync-collection") ? " sync-collection"
            : body.Contains("calendar-multiget") ? " multiget"
            : body.Contains("calendar-query") ? (body.Contains("calendar-data") ? " calendar-query+data" : " calendar-query")
            : "";
        Requests.Add($"{request.Method.Method} {path}{report}");
        RecordAttachmentRequest(request, authentication);
        if (request.Headers.TryGetValues("Prefer", out var prefer))
            PreferHeaders.Add($"{request.Method.Method} {string.Join(",", prefer)}");

        var response = Handle(request, path, body);
        response.RequestMessage = request;
        return response;
    }

    private HttpResponseMessage Handle(HttpRequestMessage request, string path, string body)
    {
        switch (request.Method.Method)
        {
            case "OPTIONS":
                var options = new HttpResponseMessage(HttpStatusCode.OK);
                if (!string.IsNullOrEmpty(DavHeader)) options.Headers.TryAddWithoutValidation("DAV", DavHeader);
                return options;
            case "PROPFIND" when path == "/":
                return MultiStatus(Response("/", $"<D:current-user-principal><D:href>{PrincipalPath}</D:href></D:current-user-principal>"));
            case "PROPFIND" when path == PrincipalPath:
                return MultiStatus(Response(PrincipalPath,
                    $"<C:calendar-home-set><D:href>{HomePath}</D:href></C:calendar-home-set>" +
                    "<C:calendar-user-address-set><D:href>mailto:user@example.test</D:href><D:href>urn:uuid:1234</D:href><D:href>mailto:null</D:href></C:calendar-user-address-set>" +
                    "<C:schedule-inbox-URL><D:href>/123/calendars/inbox/</D:href></C:schedule-inbox-URL>" +
                    "<C:schedule-outbox-URL><D:href>/123/calendars/outbox/</D:href></C:schedule-outbox-URL>"));
            case "PROPFIND" when path == HomePath:
                return HomeListing();
            case "REPORT" when IsCalendar(path) && body.Contains("sync-collection"):
                return SyncCollection(body);
            case "REPORT" when IsCalendar(path) && body.Contains("calendar-multiget"):
                return MultiGet(body);
            case "REPORT" when IsCalendar(path) && body.Contains("calendar-query"):
                if (RefuseEtagQuery && !body.Contains("calendar-data"))
                    return Error(HttpStatusCode.Forbidden, "<D:supported-report />");
                return CalendarQuery(includeData: body.Contains("calendar-data"));
            case "POST" when request.RequestUri!.Query.Contains("action=attachment-"):
                return HandleAttachmentAction(request, path);
            case "GET" when _files.ContainsKey(FileKey(request.RequestUri!)):
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_files[FileKey(request.RequestUri!)]) };
            case "GET":
                if (!_resources.TryGetValue(path, out var resource)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                CalendarDataResponses++;
                var get = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(resource.Ics, Encoding.UTF8, "text/calendar") };
                get.Headers.ETag = EntityTagHeaderValue.Parse(ETag(resource.Revision));
                return get;
            case "PUT":
                var exists = _resources.TryGetValue(path, out var existing);
                if (request.Headers.TryGetValues("If-None-Match", out _) && exists) return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                if (request.Headers.TryGetValues("If-Match", out var ifMatch) && (!exists || ifMatch.Single() != ETag(existing.Revision)))
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                _resources[path] = (body, ++_revision);
                _deletedAt.Remove(path);
                var put = new HttpResponseMessage(exists ? HttpStatusCode.NoContent : HttpStatusCode.Created);
                put.Headers.ETag = EntityTagHeaderValue.Parse(ETag(_revision));
                return put;
            case "DELETE":
                if (!_resources.TryGetValue(path, out var current)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (request.Headers.TryGetValues("If-Match", out var deleteMatch) && deleteMatch.Single() != ETag(current.Revision))
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                _resources.Remove(path);
                _deletedAt[path] = ++_revision;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            default:
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }
    }

    #region Attachments (RFC 8607)

    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private int _managedIds;

    /// <summary>"GET host/path Basic" or "... Anonymous" for every GET and POST, to check where credentials go.</summary>
    public List<string> Authentications { get; } = [];

    /// <summary>The POST attachment actions with their query, headers and body.</summary>
    public List<AttachmentAction> AttachmentActions { get; } = [];

    /// <summary>Origin of the URLs of managed attachments; iCloud serves them from another host.</summary>
    public string AttachmentOrigin { get; set; } = Origin;

    /// <summary>
    /// True answers an add with only a Location header (iCloud: no ETag, no Cal-Managed-ID, no
    /// body); false answers with ETag, Cal-Managed-ID and the updated calendar data.
    /// </summary>
    public bool BareAttachmentResponses { get; set; }

    /// <summary>Answers attachment-remove with 200 and keeps the attachment, as iCloud does.</summary>
    public bool IgnoreAttachmentRemove { get; set; }

    /// <summary>Serves a file at an absolute URL (any host) for attachment GETs.</summary>
    public void PutFile(string absoluteUrl, byte[] content) => _files[FileKey(new Uri(absoluteUrl))] = content;

    public sealed record AttachmentAction(string Path, string Query, string ContentType, string ContentDisposition, string IfMatch, byte[] Body);

    private static string FileKey(Uri uri) => uri.Host + uri.AbsolutePath;

    private void RecordAttachmentRequest(HttpRequestMessage request, DavAuthenticationProfile authentication)
    {
        if (request.Method == HttpMethod.Get || request.Method == HttpMethod.Post)
            Authentications.Add($"{request.Method.Method} {request.RequestUri!.Host}{request.RequestUri.AbsolutePath} {authentication.Kind}");
    }

    private HttpResponseMessage HandleAttachmentAction(HttpRequestMessage request, string path)
    {
        var query = request.RequestUri!.Query.TrimStart('?');
        var ifMatch = request.Headers.TryGetValues("If-Match", out var matches) ? matches.Single() : string.Empty;
        var body = request.Content?.ReadAsByteArrayAsync().GetAwaiter().GetResult() ?? [];
        AttachmentActions.Add(new AttachmentAction(
            path,
            query,
            request.Content?.Headers.ContentType?.ToString() ?? string.Empty,
            request.Content?.Headers.TryGetValues("Content-Disposition", out var disposition) == true ? disposition.Single() : string.Empty,
            ifMatch,
            body));

        if (!_resources.TryGetValue(path, out var existing)) return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (ifMatch.Length > 0 && ifMatch != ETag(existing.Revision)) return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);

        string ics;
        string managedId = string.Empty;
        string location = string.Empty;

        if (query.StartsWith("action=attachment-add", StringComparison.Ordinal))
        {
            managedId = $"mid-{++_managedIds}";
            var fileName = System.Text.RegularExpressions.Regex.Match(
                request.Content!.Headers.GetValues("Content-Disposition").Single(), "filename=\"([^\"]*)\"").Groups[1].Value;
            location = $"{AttachmentOrigin}/attachments/{managedId}/{fileName}";
            _files[FileKey(new Uri(location))] = body;
            var attach = $"ATTACH;FMTTYPE={request.Content.Headers.ContentType?.MediaType};SIZE={body.Length};FILENAME={fileName};MANAGED-ID={managedId}:{location}\r\n";
            ics = existing.Ics.Replace("END:VEVENT", attach + "END:VEVENT", StringComparison.Ordinal);
        }
        else
        {
            var removedId = Uri.UnescapeDataString(query.Split("managed-id=")[1]);
            if (IgnoreAttachmentRemove) return new HttpResponseMessage(HttpStatusCode.OK);
            ics = string.Join("\r\n", existing.Ics.Split("\r\n").Where(line => !line.Contains($"MANAGED-ID={removedId}", StringComparison.Ordinal)));
        }

        _resources[path] = (ics, ++_revision);

        if (BareAttachmentResponses)
        {
            var bare = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            if (location.Length > 0) bare.Headers.Location = new Uri(location);
            return bare;
        }

        var response = new HttpResponseMessage(managedId.Length > 0 ? HttpStatusCode.Created : HttpStatusCode.OK)
        {
            Content = new StringContent(ics, Encoding.UTF8, "text/calendar")
        };
        response.Headers.ETag = EntityTagHeaderValue.Parse(ETag(_revision));
        if (managedId.Length > 0)
        {
            response.Headers.TryAddWithoutValidation("Cal-Managed-ID", managedId);
            response.Headers.Location = new Uri(location);
        }

        return response;
    }

    #endregion

    private HttpResponseMessage HomeListing()
    {
        var reports = (AdvertiseSyncCollection ? "<D:supported-report><D:report><D:sync-collection /></D:report></D:supported-report>" : "") +
                      "<D:supported-report><D:report><C:calendar-multiget /></D:report></D:supported-report>" +
                      "<D:supported-report><D:report><C:calendar-query /></D:report></D:supported-report>";
        var token = AdvertiseSyncCollection ? $"<D:sync-token>{Token(_revision)}</D:sync-token>" : "";

        return MultiStatus(
            Response(HomePath, "<D:resourcetype><D:collection /></D:resourcetype>" +
                               "<C:default-alarm-vevent-datetime>BEGIN:VALARM&#13;\nTRIGGER:-PT15M&#13;\nACTION:DISPLAY&#13;\nEND:VALARM</C:default-alarm-vevent-datetime>") +
            Response(CalendarPath, "<D:resourcetype><D:collection /><C:calendar /></D:resourcetype><D:displayname>Work</D:displayname>" +
                                   "<D:owner><D:href>/123/principal/</D:href></D:owner>" +
                                   "<D:current-user-privilege-set><D:privilege><D:all /></D:privilege></D:current-user-privilege-set>" +
                                   $"<D:supported-report-set>{reports}</D:supported-report-set>" +
                                   "<C:supported-calendar-component-set><C:comp name=\"VEVENT\" /></C:supported-calendar-component-set>" +
                                   "<D:quota-available-bytes>1048576</D:quota-available-bytes><D:quota-used-bytes>2048</D:quota-used-bytes>" +
                                   "<C:max-attendees-per-instance>300</C:max-attendees-per-instance><C:max-resource-size>0</C:max-resource-size>" +
                                   token + $"<CS:getctag>ctag-{_revision}</CS:getctag>" +
                                   "<ICAL:calendar-color>#FF2968FF</ICAL:calendar-color><ICAL:calendar-order>1</ICAL:calendar-order>") +
            Response(TasksPath, "<D:resourcetype><D:collection /><C:calendar /></D:resourcetype><D:displayname>Reminders</D:displayname>" +
                                "<C:supported-calendar-component-set><C:comp name=\"VTODO\" /></C:supported-calendar-component-set>" +
                                "<CS:getctag>tasks-1</CS:getctag>"));
    }

    private HttpResponseMessage SyncCollection(string body)
    {
        if (!SupportsSyncCollection)
            return EmptyMultistatusForUnsupportedReports ? MultiStatus(string.Empty) : Error(HttpStatusCode.Forbidden, "<D:supported-report />");

        var token = XDocument.Parse(body).Descendants(Dav + "sync-token").Single().Value;
        var since = 0;
        if (token.Length > 0 && (!token.StartsWith("token-") || !int.TryParse(token[6..], out since) || since < OldestValidRevision))
            return Error(HttpStatusCode.Forbidden, "<D:valid-sync-token />");

        var changes = _resources.Where(item => item.Value.Revision > since)
            .Select(item => (Revision: item.Value.Revision, Xml: Response(item.Key, $"<D:getetag>{ETag(item.Value.Revision)}</D:getetag>")))
            .Concat(token.Length == 0
                ? []
                : _deletedAt.Where(item => item.Value > since)
                    .Select(item => (Revision: item.Value, Xml: $"<D:response><D:href>{item.Key}</D:href><D:status>HTTP/1.1 404 Not Found</D:status></D:response>")))
            .OrderBy(change => change.Revision)
            .ToList();

        if (SyncPageSize > 0 && changes.Count > SyncPageSize)
        {
            var page = changes.Take(SyncPageSize).ToList();
            var truncated = $"<D:response><D:href>{CalendarPath}</D:href><D:status>HTTP/1.1 507 Insufficient Storage</D:status></D:response>";
            return MultiStatus(string.Concat(page.Select(change => change.Xml)) + truncated + $"<D:sync-token>{Token(page[^1].Revision)}</D:sync-token>");
        }

        return MultiStatus(string.Concat(changes.Select(change => change.Xml)) + $"<D:sync-token>{Token(_revision)}</D:sync-token>");
    }

    private HttpResponseMessage CalendarQuery(bool includeData)
    {
        if (includeData)
            CalendarDataResponses += _resources.Count;

        return MultiStatus(string.Concat(_resources.Select(item => Response(item.Key,
            $"<D:getetag>{ETag(item.Value.Revision)}</D:getetag>" +
            (includeData ? $"<C:calendar-data>{SecurityElement.Escape(item.Value.Ics)}</C:calendar-data>" : "")))));
    }

    private HttpResponseMessage MultiGet(string body)
    {
        if (!SupportsMultiget)
            return EmptyMultistatusForUnsupportedReports ? MultiStatus(string.Empty) : Error(HttpStatusCode.Forbidden, "<D:supported-report />");

        var hrefs = XDocument.Parse(body).Descendants(Dav + "href").Select(element => element.Value).ToList();
        if (hrefs.Any(href => !href.StartsWith('/')))
            return new HttpResponseMessage(HttpStatusCode.BadRequest);

        return MultiStatus(string.Concat(hrefs.Select(href =>
        {
            if (!_resources.TryGetValue(href, out var resource))
                return $"<D:response><D:href>{href}</D:href><D:status>HTTP/1.1 404 Not Found</D:status></D:response>";

            CalendarDataResponses++;
            return Response(href, $"<D:getetag>{ETag(resource.Revision)}</D:getetag><C:calendar-data>{SecurityElement.Escape(resource.Ics)}</C:calendar-data>");
        })));
    }

    /// <summary>Servers accept the collection with or without its trailing slash.</summary>
    private static bool IsCalendar(string path) => path.TrimEnd('/') == CalendarPath.TrimEnd('/');

    private static string Token(int revision) => $"token-{revision}";
    private static string ETag(int revision) => $"\"rev-{revision}\"";

    private static string Response(string href, string properties)
        => $"<D:response><D:href>{href}</D:href><D:propstat><D:prop>{properties}</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>";

    private static HttpResponseMessage MultiStatus(string responses) => new(HttpStatusCode.MultiStatus)
    {
        Content = new StringContent(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><D:multistatus xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\" " +
            $"xmlns:CS=\"http://calendarserver.org/ns/\" xmlns:ICAL=\"http://apple.com/ns/ical/\">{responses}</D:multistatus>",
            Encoding.UTF8, "application/xml")
    };

    private static HttpResponseMessage Error(HttpStatusCode status, string condition) => new(status)
    {
        Content = new StringContent($"<D:error xmlns:D=\"DAV:\">{condition}</D:error>", Encoding.UTF8, "application/xml")
    };
}
