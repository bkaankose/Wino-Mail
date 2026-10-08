using System;
using System.Collections.Generic;
using System.IO;
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
using Serilog;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.CardDav;
using Wino.Services.Dav;

namespace Wino.Services.CardDav;

/// <summary>
/// CardDAV protocol requests (RFC 6352) with WebDAV sync (RFC 6578). Holds no state;
/// what to request and what to do with the answer is decided by the synchronization engine.
/// </summary>
public sealed class CardDavClient : ICardDavClient
{
    private const string DavNamespace = "DAV:";
    private const string CardDavNamespace = "urn:ietf:params:xml:ns:carddav";
    private const string CalendarServerNamespace = "http://calendarserver.org/ns/";
    private const long MaximumResourceBytes = 64L * 1024 * 1024;
    private static readonly ILogger Logger = Log.ForContext<CardDavClient>();
    private static readonly HttpMethod PropFindMethod = new("PROPFIND");
    private static readonly HttpMethod ReportMethod = new("REPORT");
    private static readonly HttpMethod PropPatchMethod = new("PROPPATCH");
    private static readonly HttpMethod MkColMethod = new("MKCOL");
    private readonly IDavTransport _transport;
    private readonly IDavMultistatusReader _multistatusReader;
    private readonly IDavResponseHandler _responseHandler;

    public CardDavClient(
        IDavTransport transport,
        IDavMultistatusReader multistatusReader,
        IDavResponseHandler responseHandler = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _multistatusReader = multistatusReader ?? throw new ArgumentNullException(nameof(multistatusReader));
        _responseHandler = responseHandler ?? new DavResponseHandler();
    }

    public async Task<CardDavDiscoveryResult> DiscoverAsync(CardDavConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        const string properties = "<D:current-user-principal /><C:addressbook-home-set /><D:resourcetype />";
        var contextUri = settings.ServiceUri ?? BuildWellKnownUri(settings.AccountAddress);
        var context = await PropFindAsync(settings, contextUri, "0", properties, cancellationToken).ConfigureAwait(false);
        var contextResponse = FindRequestedResponse(context, contextUri);
        var principalHref = Property(contextResponse, DavNamespace, "current-user-principal")?.Value;
        var principalUri = string.IsNullOrWhiteSpace(principalHref) ? contextUri : Resolve(contextUri, principalHref);
        var homeHref = Property(contextResponse, CardDavNamespace, "addressbook-home-set")?.Value;

        if (string.IsNullOrWhiteSpace(homeHref) && principalUri != contextUri)
        {
            var principal = await PropFindAsync(settings, principalUri, "0", properties, cancellationToken).ConfigureAwait(false);
            homeHref = Property(FindRequestedResponse(principal, principalUri), CardDavNamespace, "addressbook-home-set")?.Value;
        }

        var homeUri = string.IsNullOrWhiteSpace(homeHref) ? principalUri : Resolve(principalUri, homeHref);
        var listing = await ListAddressBooksAsync(settings, homeUri, cancellationToken).ConfigureAwait(false);

        return new CardDavDiscoveryResult
        {
            ContextUri = contextUri,
            PrincipalUri = principalUri,
            AddressBookHomeUri = homeUri,
            SupportsAddressBookCreation = listing.SupportsAddressBookCreation,
            AddressBooks = listing.AddressBooks
        };
    }

    public async Task<CardDavDiscoveryResult> ListAddressBooksAsync(
        CardDavConnectionSettings settings,
        Uri addressBookHomeUri,
        CancellationToken cancellationToken = default)
    {
        Validate(settings);
        ArgumentNullException.ThrowIfNull(addressBookHomeUri);
        const string properties =
            "<D:resourcetype /><D:displayname /><D:current-user-privilege-set /><D:supported-report-set />" +
            "<D:sync-token /><CS:getctag /><C:supported-address-data />";
        var listing = await PropFindAsync(settings, addressBookHomeUri, "1", properties, cancellationToken).ConfigureAwait(false);
        var homeResponse = listing.Responses.FirstOrDefault(response => IsSameResource(Resolve(addressBookHomeUri, response.Href), addressBookHomeUri));
        var homePrivileges = Property(homeResponse, DavNamespace, "current-user-privilege-set")?.Xml;
        var books = listing.Responses
            .Where(response => ContainsElement(Property(response, DavNamespace, "resourcetype")?.Xml, CardDavNamespace, "addressbook"))
            .Select(response => ParseAddressBook(response, addressBookHomeUri))
            .GroupBy(book => book.ExactHref, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        return new CardDavDiscoveryResult
        {
            AddressBookHomeUri = addressBookHomeUri,
            SupportsAddressBookCreation = ContainsElement(homePrivileges, DavNamespace, "bind") ||
                                          ContainsElement(homePrivileges, DavNamespace, "all"),
            AddressBooks = books
        };
    }

    public async Task<CardDavSyncPage> SyncCollectionAsync(
        CardDavConnectionSettings settings,
        CardDavAddressBook addressBook,
        string syncToken,
        CancellationToken cancellationToken = default)
    {
        Validate(settings);
        var collectionUri = CollectionUri(addressBook);
        var tokenXml = string.IsNullOrEmpty(syncToken) ? "<D:sync-token />" : $"<D:sync-token>{Escape(syncToken)}</D:sync-token>";
        var body = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <D:sync-collection xmlns:D="DAV:">
              {tokenXml}
              <D:sync-level>1</D:sync-level>
              <D:prop><D:getetag /></D:prop>
            </D:sync-collection>
            """;
        var multistatus = await SendMultistatusAsync(settings, ReportMethod, collectionUri, "0", body, cancellationToken).ConfigureAwait(false);

        // The collection itself only appears to report that the listing was cut short
        // (RFC 6578 section 3.6) or that the whole request failed.
        var collectionResponse = multistatus.Responses.FirstOrDefault(response => IsSameResource(Resolve(collectionUri, response.Href), collectionUri));
        var isTruncated = collectionResponse?.StatusCode == 507 ||
                          collectionResponse?.PropertyStatuses.Any(status => status.StatusCode == 507) == true;
        if (collectionResponse?.StatusCode is >= 400 and not 507)
            throw new DavRequestException(collectionResponse.StatusCode.Value, "The DAV server could not synchronize the collection.", collectionResponse.ErrorNames);

        return new CardDavSyncPage
        {
            Changes = MemberResources(multistatus, collectionUri),
            NextSyncToken = multistatus.SyncToken,
            IsTruncated = isTruncated
        };
    }

    public async Task<IReadOnlyList<CardDavResourceChange>> EnumerateResourcesAsync(
        CardDavConnectionSettings settings,
        CardDavAddressBook addressBook,
        CancellationToken cancellationToken = default)
    {
        Validate(settings);
        var collectionUri = CollectionUri(addressBook);
        var multistatus = await PropFindAsync(settings, collectionUri, "1", "<D:resourcetype /><D:getetag />", cancellationToken).ConfigureAwait(false);
        var collectionResponse = multistatus.Responses.FirstOrDefault(response => IsSameResource(Resolve(collectionUri, response.Href), collectionUri));
        if (collectionResponse?.StatusCode is >= 400)
            throw new DavRequestException(collectionResponse.StatusCode.Value, "The DAV server could not enumerate the collection.", collectionResponse.ErrorNames);

        return MemberResources(multistatus, collectionUri).Where(resource => !resource.IsDeleted).ToList();
    }

    public async Task<IReadOnlyList<CardDavResourceChange>> MultiGetAsync(
        CardDavConnectionSettings settings,
        CardDavAddressBook addressBook,
        IReadOnlyList<string> hrefs,
        CancellationToken cancellationToken = default)
    {
        if (hrefs is null || hrefs.Count == 0)
            return [];

        Validate(settings);
        var collectionUri = CollectionUri(addressBook);

        // Servers answer with their own spelling of an href (relative, re-encoded). The
        // caller tracks resources by the href it asked for, so answers are mapped back.
        var requested = new Dictionary<string, string>(StringComparer.Ordinal);
        var hrefXml = new StringBuilder();
        foreach (var href in hrefs)
        {
            var resourceUri = Resolve(collectionUri, href);
            requested[ResourceKey(resourceUri)] = href;

            // Use the RFC 4918 absolute-path form for resources on this server. iCloud
            // rejects absolute URLs inside multiget even though it returns them in REPORTs.
            var sameOrigin = string.Equals(resourceUri.GetLeftPart(UriPartial.Authority),
                collectionUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
            hrefXml.Append("<D:href>").Append(Escape(sameOrigin ? resourceUri.PathAndQuery : resourceUri.AbsoluteUri)).Append("</D:href>");
        }

        var body = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <C:addressbook-multiget xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:carddav">
              <D:prop><D:getetag /><C:address-data /></D:prop>
              {hrefXml}
            </C:addressbook-multiget>
            """;

        // The hrefs, not Depth, scope a multiget (RFC 6352 section 8.7).
        var multistatus = await SendMultistatusAsync(settings, ReportMethod, collectionUri, "0", body, cancellationToken).ConfigureAwait(false);
        var results = new List<CardDavResourceChange>(multistatus.Responses.Count);
        foreach (var response in multistatus.Responses)
        {
            if (string.IsNullOrWhiteSpace(response.Href) ||
                !requested.TryGetValue(ResourceKey(Resolve(collectionUri, response.Href)), out var requestedHref))
                continue;

            results.Add(new CardDavResourceChange
            {
                ExactHref = requestedHref,
                ETag = Property(response, DavNamespace, "getetag")?.Value,
                VCard = Property(response, CardDavNamespace, "address-data")?.Value,
                IsDeleted = response.StatusCode == 404
            });
        }

        return results;
    }

    public async Task<CardDavResourceChange> GetResourceAsync(
        CardDavConnectionSettings settings,
        string exactHref,
        CancellationToken cancellationToken = default)
    {
        Validate(settings);
        using var request = new HttpRequestMessage(HttpMethod.Get, exactHref);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/vcard"));
        using var response = await SendAsync(settings, request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new CardDavResourceChange { ExactHref = exactHref, IsDeleted = true };

        await _responseHandler.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return new CardDavResourceChange
        {
            ExactHref = exactHref,
            ETag = response.Headers.ETag?.ToString(),
            VCard = await ReadBoundedStringAsync(response.Content, cancellationToken).ConfigureAwait(false)
        };
    }

    public async Task<CardDavWriteResult> PutResourceAsync(
        CardDavConnectionSettings settings,
        string exactHref,
        string vcard,
        string ifMatch = null,
        bool createOnly = false,
        CancellationToken cancellationToken = default)
    {
        Validate(settings);
        using var request = new HttpRequestMessage(HttpMethod.Put, exactHref)
        {
            Content = new StringContent(vcard ?? throw new ArgumentNullException(nameof(vcard)), Encoding.UTF8, "text/vcard")
        };
        if (createOnly) request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        else if (!string.IsNullOrWhiteSpace(ifMatch)) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);

        using var response = await SendAsync(settings, request, cancellationToken).ConfigureAwait(false);
        await _responseHandler.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        // A weak or missing ETag means the server stored something other than the bytes
        // sent (RFC 6352 section 6.3.2.3); the next synchronization downloads the result.
        var etag = response.Headers.ETag;
        return new CardDavWriteResult
        {
            ExactHref = response.Headers.Location is null
                ? exactHref
                : Resolve(new Uri(exactHref), response.Headers.Location.OriginalString).AbsoluteUri,
            ETag = etag is null || etag.IsWeak ? null : etag.ToString()
        };
    }

    public async Task DeleteResourceAsync(
        CardDavConnectionSettings settings,
        string exactHref,
        string ifMatch = null,
        CancellationToken cancellationToken = default)
    {
        Validate(settings);
        using var request = new HttpRequestMessage(HttpMethod.Delete, exactHref);
        if (!string.IsNullOrWhiteSpace(ifMatch)) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        using var response = await SendAsync(settings, request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await _responseHandler.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CardDavAddressBook> CreateAddressBookAsync(
        CardDavConnectionSettings settings,
        string homeHref,
        string collectionName,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        Validate(settings);
        var safeName = Uri.EscapeDataString(string.IsNullOrWhiteSpace(collectionName) ? Guid.NewGuid().ToString("N") : collectionName.Trim());
        var target = new Uri(new Uri(EnsureTrailingSlash(homeHref)), safeName + "/");
        var properties = $"<D:set><D:prop><D:resourcetype><D:collection /><C:addressbook /></D:resourcetype><D:displayname>{Escape(displayName)}</D:displayname></D:prop></D:set>";
        using var request = XmlRequest(MkColMethod, target,
            $"<D:mkcol xmlns:D=\"DAV:\" xmlns:C=\"{CardDavNamespace}\">{properties}</D:mkcol>", null);
        using var response = await SendAsync(settings, request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.NotImplemented)
        {
            // No extended MKCOL (RFC 5689): create a plain collection and type it afterwards.
            using var plainMkCol = new HttpRequestMessage(MkColMethod, target);
            using var plainResponse = await SendAsync(settings, plainMkCol, cancellationToken).ConfigureAwait(false);
            await _responseHandler.EnsureSuccessAsync(plainResponse, cancellationToken).ConfigureAwait(false);
            using var propertyRequest = XmlRequest(PropPatchMethod, target,
                $"<D:propertyupdate xmlns:D=\"DAV:\" xmlns:C=\"{CardDavNamespace}\">{properties}</D:propertyupdate>", null);
            using var propertyResponse = await SendAsync(settings, propertyRequest, cancellationToken).ConfigureAwait(false);
            await _responseHandler.EnsureSuccessAsync(propertyResponse, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _responseHandler.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }

        return new CardDavAddressBook { ExactHref = target.AbsoluteUri, DisplayName = displayName };
    }

    public async Task RenameAddressBookAsync(
        CardDavConnectionSettings settings,
        string exactHref,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        Validate(settings);
        var body = $"<D:propertyupdate xmlns:D=\"DAV:\"><D:set><D:prop><D:displayname>{Escape(displayName)}</D:displayname></D:prop></D:set></D:propertyupdate>";
        using var request = XmlRequest(PropPatchMethod, new Uri(exactHref), body, null);
        using var response = await SendAsync(settings, request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.MultiStatus)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var multistatus = await _multistatusReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            var failure = multistatus.Responses.SelectMany(item => item.PropertyStatuses).FirstOrDefault(item => item.StatusCode is < 200 or >= 300);
            if (failure is not null) throw new DavRequestException(failure.StatusCode ?? 500, "The server rejected the address-book rename.", failure.ErrorNames);
            return;
        }

        await _responseHandler.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAddressBookAsync(CardDavConnectionSettings settings, string exactHref, CancellationToken cancellationToken = default)
        => DeleteResourceAsync(settings, exactHref, cancellationToken: cancellationToken);

    private Task<DavMultistatus> PropFindAsync(
        CardDavConnectionSettings settings,
        Uri uri,
        string depth,
        string properties,
        CancellationToken cancellationToken)
        => SendMultistatusAsync(settings, PropFindMethod, uri, depth,
            $"<?xml version=\"1.0\" encoding=\"utf-8\"?><D:propfind xmlns:D=\"DAV:\" xmlns:C=\"{CardDavNamespace}\" xmlns:CS=\"{CalendarServerNamespace}\"><D:prop>{properties}</D:prop></D:propfind>",
            cancellationToken);

    private async Task<DavMultistatus> SendMultistatusAsync(
        CardDavConnectionSettings settings,
        HttpMethod method,
        Uri uri,
        string depth,
        string body,
        CancellationToken cancellationToken)
    {
        using var request = XmlRequest(method, uri, body, depth);
        using var response = await SendAsync(settings, request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.MultiStatus)
        {
            await _responseHandler.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            throw new DavRequestException((int)response.StatusCode, "The DAV server returned a non-multistatus response.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var multistatus = await _multistatusReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        Logger.Debug("CardDAV {Method} {Path} returned {ResponseCount} responses", method.Method, uri.AbsolutePath, multistatus.Responses.Count);
        return multistatus;
    }

    private async Task<HttpResponseMessage> SendAsync(CardDavConnectionSettings settings, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await _transport.SendAsync(request, settings.Authentication, cancellationToken).ConfigureAwait(false);
        Logger.Debug("CardDAV {Method} {Path} -> {StatusCode}", request.Method.Method, request.RequestUri?.AbsolutePath, (int)response.StatusCode);
        return response;
    }

    private static HttpRequestMessage XmlRequest(HttpMethod method, Uri uri, string body, string depth)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = new StringContent(body, Encoding.UTF8, "application/xml")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/xml"));
        if (depth is not null) request.Headers.TryAddWithoutValidation("Depth", depth);
        return request;
    }

    private static async Task<string> ReadBoundedStringAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResourceBytes)
            throw new InvalidDataException("The DAV response exceeded the configured size limit.");

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (destination.Length + read > MaximumResourceBytes)
                throw new InvalidDataException("The DAV response exceeded the configured size limit.");

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(destination.GetBuffer(), 0, checked((int)destination.Length));
    }

    /// <summary>
    /// The members a listing or sync REPORT describes. The collection itself and nested
    /// collections are not address objects and are left out.
    /// </summary>
    private static List<CardDavResourceChange> MemberResources(DavMultistatus multistatus, Uri collectionUri)
    {
        var members = new List<CardDavResourceChange>(multistatus.Responses.Count);
        foreach (var response in multistatus.Responses)
        {
            if (string.IsNullOrWhiteSpace(response.Href))
                continue;

            var resourceUri = Resolve(collectionUri, response.Href);
            if (IsSameResource(resourceUri, collectionUri) ||
                resourceUri.AbsolutePath.EndsWith('/') ||
                ContainsElement(Property(response, DavNamespace, "resourcetype")?.Xml, DavNamespace, "collection"))
                continue;

            // Only the response status describes the resource. A propstat status describes
            // a property, and a missing property must never read as a deleted contact.
            members.Add(new CardDavResourceChange
            {
                ExactHref = resourceUri.AbsoluteUri,
                ETag = Property(response, DavNamespace, "getetag")?.Value,
                IsDeleted = response.StatusCode == 404
            });
        }

        return members;
    }

    private static CardDavAddressBook ParseAddressBook(DavResponseItem response, Uri baseUri)
    {
        var reports = Property(response, DavNamespace, "supported-report-set")?.Xml;
        var addressData = Property(response, CardDavNamespace, "supported-address-data")?.Xml;
        var privileges = Property(response, DavNamespace, "current-user-privilege-set")?.Xml;
        return new CardDavAddressBook
        {
            ExactHref = Resolve(baseUri, response.Href).AbsoluteUri,
            DisplayName = Property(response, DavNamespace, "displayname")?.Value,
            SyncToken = Property(response, DavNamespace, "sync-token")?.Value,
            CollectionTag = Property(response, CalendarServerNamespace, "getctag")?.Value,
            IsReadOnly = !string.IsNullOrWhiteSpace(privileges) &&
                         !ContainsElement(privileges, DavNamespace, "all") &&
                         !ContainsElement(privileges, DavNamespace, "write") &&
                         !ContainsElement(privileges, DavNamespace, "write-content"),
            SupportsSyncCollection = ContainsElement(reports, DavNamespace, "sync-collection"),
            SupportsMultiget = ContainsElement(reports, CardDavNamespace, "addressbook-multiget"),
            SupportsVCard4 = SupportsVCardVersion(addressData, "4.0")
        };
    }

    private static DavResponseItem FindRequestedResponse(DavMultistatus multistatus, Uri requestUri)
        => multistatus.Responses.FirstOrDefault(response => IsSameResource(Resolve(requestUri, response.Href), requestUri))
           ?? multistatus.Responses.FirstOrDefault(response => response.PropertyStatuses.Any(status => status.StatusCode is >= 200 and < 300))
           ?? throw new DavRequestException(500, "The DAV response did not contain a successful request resource.");

    private static DavProperty Property(DavResponseItem response, string xmlNamespace, string name)
        => response?.PropertyStatuses.Where(status => status.StatusCode is null or (>= 200 and < 300))
            .SelectMany(status => status.Properties)
            .FirstOrDefault(property => property.Namespace == xmlNamespace && property.Name == name);

    private static bool SupportsVCardVersion(string xml, string version)
    {
        if (string.IsNullOrWhiteSpace(xml)) return false;

        XNamespace cardDav = CardDavNamespace;
        return XElement.Parse(xml).Elements(cardDav + "address-data-type").Any(element =>
            string.Equals((string)element.Attribute("content-type"), "text/vcard", StringComparison.OrdinalIgnoreCase) &&
            string.Equals((string)element.Attribute("version"), version, StringComparison.Ordinal));
    }

    private static bool ContainsElement(string xml, string xmlNamespace, string localName)
    {
        if (string.IsNullOrWhiteSpace(xml)) return false;
        try
        {
            return XElement.Parse(xml).DescendantsAndSelf().Any(item => item.Name.NamespaceName == xmlNamespace && item.Name.LocalName == localName);
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static Uri BuildWellKnownUri(string accountAddress)
    {
        var separator = accountAddress?.LastIndexOf('@') ?? -1;
        var domain = separator >= 0 ? accountAddress[(separator + 1)..] : accountAddress;
        if (string.IsNullOrWhiteSpace(domain)) throw new ArgumentException("A CardDAV service URL or account domain is required.");
        return new Uri($"https://{domain}/.well-known/carddav");
    }

    private static Uri Resolve(Uri baseUri, string href)
        => Uri.TryCreate(href, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https"
            ? absolute
            : new Uri(baseUri, href ?? string.Empty);

    /// <summary>
    /// Identity of a resource regardless of how a server spells its href: percent-encoding
    /// and a collection's trailing slash both vary (iCloud omits the slash).
    /// </summary>
    private static string ResourceKey(Uri uri) => Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');

    private static bool IsSameResource(Uri left, Uri right)
        => string.Equals(ResourceKey(left), ResourceKey(right), StringComparison.Ordinal);

    private static Uri CollectionUri(CardDavAddressBook addressBook)
    {
        if (string.IsNullOrWhiteSpace(addressBook?.ExactHref))
            throw new ArgumentException("A CardDAV address-book href is required.");

        return new Uri(addressBook.ExactHref);
    }

    private static string EnsureTrailingSlash(string href) => href.EndsWith('/') ? href : href + "/";
    private static string Escape(string value) => SecurityElement.Escape(value ?? string.Empty);

    private static void Validate(CardDavConnectionSettings settings)
    {
        if (settings?.Authentication is null) throw new ArgumentException("CardDAV authentication is required.");
        if (settings.ServiceUri is null && string.IsNullOrWhiteSpace(settings.AccountAddress))
            throw new ArgumentException("A CardDAV service URL or account address is required.");
    }
}
