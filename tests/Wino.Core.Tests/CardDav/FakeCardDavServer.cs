using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Xml.Linq;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.CardDav;

namespace Wino.Core.Tests.CardDav;

/// <summary>
/// An in-memory CardDAV server with one address book, modeled on iCloud: a single
/// collection that holds person and group vCards, sync-collection with numbered tokens,
/// multiget that only accepts absolute-path hrefs, and collection hrefs without a slash.
/// </summary>
internal sealed class FakeCardDavServer : IDavTransport
{
    private static readonly XNamespace Dav = "DAV:";
    private readonly Dictionary<string, (string VCard, int Revision)> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _deletedAt = new(StringComparer.Ordinal);
    private int _revision = 1;

    public const string Origin = "https://p42-contacts.example.test";
    public const string HomePath = "/1234/carddavhome/";
    public const string BookPath = "/1234/carddavhome/card/";
    public static string BookHref => Origin + BookPath;

    public List<string> Requests { get; } = [];
    public bool SupportsSyncCollection { get; set; } = true;
    public bool SupportsMultiget { get; set; } = true;

    /// <summary>Tokens older than this revision are refused, as after server-side token expiry.</summary>
    public int OldestValidRevision { get; set; }
    public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }

    public string HrefOf(string name) => BookHref + name + ".vcf";
    public string VCardOf(string name) => _resources[BookPath + name + ".vcf"].VCard;
    public bool Has(string name) => _resources.ContainsKey(BookPath + name + ".vcf");
    public IReadOnlyCollection<string> Names => _resources.Keys.Select(path => path[BookPath.Length..^4]).ToList();

    public void Put(string name, string vcard)
    {
        var path = BookPath + name + ".vcf";
        _resources[path] = (vcard, ++_revision);
        _deletedAt.Remove(path);
    }

    public void Delete(string name)
    {
        var path = BookPath + name + ".vcf";
        if (_resources.Remove(path)) _deletedAt[path] = ++_revision;
    }

    public static string Person(string uid, string name, string? email = null)
        => $"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:{uid}\r\nN:{name};;;;\r\nFN:{name}\r\n" +
           (email is null ? "" : $"EMAIL;type=INTERNET;type=HOME;type=pref:{email}\r\n") + "END:VCARD\r\n";

    public static string Group(string uid, string name, params string[] memberUids)
        => $"BEGIN:VCARD\r\nVERSION:3.0\r\nUID:{uid}\r\nN:{name}\r\nFN:{name}\r\nX-ADDRESSBOOKSERVER-KIND:group\r\n" +
           string.Concat(memberUids.Select(member => $"X-ADDRESSBOOKSERVER-MEMBER:urn:uuid:{member}\r\n")) + "END:VCARD\r\n";

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, DavAuthenticationProfile authentication, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri!.AbsolutePath;
        var report = body.Contains("sync-collection") ? " sync-collection" : body.Contains("addressbook-multiget") ? " multiget" : "";
        Requests.Add($"{request.Method.Method} {path}{report}");

        var response = Intercept?.Invoke(request) ?? Handle(request, path, body);
        response.RequestMessage = request;
        return response;
    }

    private HttpResponseMessage Handle(HttpRequestMessage request, string path, string body)
    {
        switch (request.Method.Method)
        {
            case "PROPFIND" when path == "/":
                return MultiStatus(Response("/", "<D:current-user-principal><D:href>/1234/principal/</D:href></D:current-user-principal>"));
            case "PROPFIND" when path == "/1234/principal/":
                return MultiStatus(Response(path, $"<C:addressbook-home-set><D:href>{Origin}:443{HomePath}</D:href></C:addressbook-home-set>"));
            case "PROPFIND" when path == HomePath:
                var reports = (SupportsSyncCollection ? "<D:supported-report><D:report><D:sync-collection /></D:report></D:supported-report>" : "") +
                              (SupportsMultiget ? "<D:supported-report><D:report><C:addressbook-multiget /></D:report></D:supported-report>" : "");
                return MultiStatus(
                    Response(HomePath, "<D:resourcetype><D:collection /></D:resourcetype><D:current-user-privilege-set><D:privilege><D:read /></D:privilege></D:current-user-privilege-set>") +
                    Response(BookPath, "<D:resourcetype><D:collection /><C:addressbook /></D:resourcetype><D:displayname>Card</D:displayname>" +
                        "<D:current-user-privilege-set><D:privilege><D:write /></D:privilege></D:current-user-privilege-set>" +
                        $"<D:supported-report-set>{reports}</D:supported-report-set>" +
                        (SupportsSyncCollection ? $"<D:sync-token>{Token(_revision)}</D:sync-token>" : "") +
                        $"<CS:getctag>ctag-{_revision}</CS:getctag>"));
            case "PROPFIND" when path == BookPath:
                return MultiStatus(Response(BookPath.TrimEnd('/'), "<D:resourcetype><D:collection /><C:addressbook /></D:resourcetype>") +
                    string.Concat(_resources.Select(item => Response(item.Key, $"<D:resourcetype /><D:getetag>{ETag(item.Value.Revision)}</D:getetag>"))));
            case "REPORT" when path == BookPath && body.Contains("sync-collection"):
                return SyncCollection(body);
            case "REPORT" when path == BookPath && body.Contains("addressbook-multiget"):
                return MultiGet(body);
            case "GET":
                if (!_resources.TryGetValue(path, out var resource)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                var get = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(resource.VCard, Encoding.UTF8, "text/vcard") };
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
                if (!_resources.Remove(path)) return new HttpResponseMessage(HttpStatusCode.NotFound);
                _deletedAt[path] = ++_revision;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            default:
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }
    }

    private HttpResponseMessage SyncCollection(string body)
    {
        if (!SupportsSyncCollection)
            return Error(HttpStatusCode.Forbidden, "<D:supported-report />");

        var token = XDocument.Parse(body).Descendants(Dav + "sync-token").Single().Value;
        var since = 0;
        if (token.Length > 0 && (!token.StartsWith("token-") || !int.TryParse(token[6..], out since) || since < OldestValidRevision))
            return Error(HttpStatusCode.Forbidden, "<D:valid-sync-token />");

        var changed = _resources.Where(item => item.Value.Revision > since)
            .Select(item => Response(item.Key, $"<D:getetag>{ETag(item.Value.Revision)}</D:getetag>"));
        var deleted = token.Length == 0
            ? []
            : _deletedAt.Where(item => item.Value > since)
                .Select(item => $"<D:response><D:href>{item.Key}</D:href><D:status>HTTP/1.1 404 Not Found</D:status></D:response>");
        return MultiStatus(string.Concat(changed) + string.Concat(deleted) + $"<D:sync-token>{Token(_revision)}</D:sync-token>");
    }

    private HttpResponseMessage MultiGet(string body)
    {
        if (!SupportsMultiget)
            return Error(HttpStatusCode.Forbidden, "<D:supported-report />");

        var hrefs = XDocument.Parse(body).Descendants(Dav + "href").Select(element => element.Value).ToList();
        if (hrefs.Any(href => !href.StartsWith('/')))
            return new HttpResponseMessage(HttpStatusCode.BadRequest);

        return MultiStatus(string.Concat(hrefs.Select(href => _resources.TryGetValue(href, out var resource)
            ? Response(href, $"<D:getetag>{ETag(resource.Revision)}</D:getetag><C:address-data>{SecurityElement.Escape(resource.VCard)}</C:address-data>")
            : $"<D:response><D:href>{href}</D:href><D:status>HTTP/1.1 404 Not Found</D:status></D:response>")));
    }

    private static string Token(int revision) => $"token-{revision}";
    private static string ETag(int revision) => $"\"C={revision}@U=fake\"";

    private static string Response(string href, string properties)
        => $"<D:response><D:href>{href}</D:href><D:propstat><D:prop>{properties}</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>";

    private static HttpResponseMessage MultiStatus(string responses) => new(HttpStatusCode.MultiStatus)
    {
        Content = new StringContent(
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><D:multistatus xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:carddav\" xmlns:CS=\"http://calendarserver.org/ns/\">{responses}</D:multistatus>",
            Encoding.UTF8, "application/xml")
    };

    private static HttpResponseMessage Error(HttpStatusCode status, string condition) => new(status)
    {
        Content = new StringContent($"<D:error xmlns:D=\"DAV:\">{condition}</D:error>", Encoding.UTF8, "application/xml")
    };
}
