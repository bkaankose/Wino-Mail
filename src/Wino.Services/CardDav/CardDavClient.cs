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
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.CardDav;
using Wino.Services.Dav;

namespace Wino.Services.CardDav;

public sealed class CardDavClient : ICardDavClient
{
    private const string DavNamespace = "DAV:";
    private const string CardDavNamespace = "urn:ietf:params:xml:ns:carddav";
    private const string CalendarServerNamespace = "http://calendarserver.org/ns/";
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
        var contextUri = settings.ServiceUri ?? BuildWellKnownUri(settings.AccountAddress);
        var context = await PropFindAsync(settings, contextUri, "0", DiscoveryProperties(), cancellationToken).ConfigureAwait(false);
        var contextResponse = FindSuccessfulResponse(context, contextUri);
        var principalHref = Property(contextResponse, DavNamespace, "current-user-principal")?.Value;
        var principalUri = string.IsNullOrWhiteSpace(principalHref) ? contextUri : Resolve(contextUri, principalHref);
        var homeHref = Property(contextResponse, CardDavNamespace, "addressbook-home-set")?.Value;

        if (string.IsNullOrWhiteSpace(homeHref) && principalUri != contextUri)
        {
            var principal = await PropFindAsync(settings, principalUri, "0", DiscoveryProperties(), cancellationToken).ConfigureAwait(false);
            homeHref = Property(FindSuccessfulResponse(principal, principalUri), CardDavNamespace, "addressbook-home-set")?.Value;
        }

        var homeUri = string.IsNullOrWhiteSpace(homeHref) ? principalUri : Resolve(principalUri, homeHref);
        var listing = await PropFindAsync(settings, homeUri, "1", CollectionProperties(), cancellationToken).ConfigureAwait(false);
        var homeResponse = listing.Responses.FirstOrDefault(response => HrefEquals(response.Href, homeUri, homeUri));
        var homePrivileges = Property(homeResponse, DavNamespace, "current-user-privilege-set")?.Xml;
        var books = listing.Responses
            .Where(response => response.PropertyStatuses.Any(status => status.StatusCode == 200 &&
                IsAddressBook(Property(status, DavNamespace, "resourcetype"))))
            .Select(response => ParseAddressBook(response, homeUri))
            .GroupBy(book => book.ExactHref, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        return new CardDavDiscoveryResult
        {
            ContextUri = contextUri,
            PrincipalUri = principalUri,
            AddressBookHomeUri = homeUri,
            SupportsAddressBookCreation = ContainsElement(homePrivileges, DavNamespace, "bind"),
            AddressBooks = books
        };
    }

    public async Task<CardDavSyncPage> SyncCollectionAsync(
        CardDavConnectionSettings settings,
        CardDavAddressBook addressBook,
        string syncToken,
        int limit,
        CancellationToken cancellationToken = default)
    {
        Validate(settings);
        ValidateAddressBook(addressBook);
        var tokenXml = string.IsNullOrEmpty(syncToken) ? "<D:sync-token />" : $"<D:sync-token>{Escape(syncToken)}</D:sync-token>";
        var limitXml = limit > 0 ? $"<D:limit><D:nresults>{limit}</D:nresults></D:limit>" : string.Empty;
        var body = $"""
            <D:sync-collection xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:carddav">
              {tokenXml}
              <D:sync-level>1</D:sync-level>
              {limitXml}
              <D:prop><D:getetag /><C:address-data /></D:prop>
            </D:sync-collection>
            """;
        var collectionUri = new Uri(addressBook.ExactHref);
        var multistatus = await SendMultistatusAsync(settings, ReportMethod, collectionUri, "0", body, cancellationToken).ConfigureAwait(false);
        var changes = multistatus.Responses.Select(response => ParseResourceChange(response, collectionUri)).ToList();
        var collectionResponse = multistatus.Responses.FirstOrDefault(response => HrefEquals(response.Href, collectionUri, collectionUri));
        return new CardDavSyncPage
        {
            Changes = changes.Where(change => !HrefEquals(change.ExactHref, collectionUri, collectionUri)).ToList(),
            NextSyncToken = multistatus.SyncToken,
            IsTruncated = collectionResponse?.StatusCode == 507 ||
                          collectionResponse?.PropertyStatuses.Any(status => status.StatusCode == 507) == true
        };
    }

    public async Task<IReadOnlyList<CardDavResourceChange>> EnumerateResourcesAsync(
        CardDavConnectionSettings settings,
        CardDavAddressBook addressBook,
        CancellationToken cancellationToken = default)
    {
        Validate(settings);
        ValidateAddressBook(addressBook);
        var collectionUri = new Uri(addressBook.ExactHref);
        var multistatus = await PropFindAsync(
            settings,
            collectionUri,
            "1",
            "<D:resourcetype /><D:getetag /><D:getcontenttype />",
            cancellationToken).ConfigureAwait(false);
        return multistatus.Responses
            .Where(response => !HrefEquals(response.Href, collectionUri, collectionUri))
            .Select(response => ParseResourceChange(response, collectionUri))
            .ToList();
    }

    public Task<IReadOnlyList<CardDavResourceChange>> MultiGetAsync(
        CardDavConnectionSettings settings,
        CardDavAddressBook addressBook,
        IReadOnlyList<string> hrefs,
        CancellationToken cancellationToken = default)
    {
        if (hrefs is null || hrefs.Count == 0)
            return Task.FromResult<IReadOnlyList<CardDavResourceChange>>([]);
        var hrefXml = string.Join(string.Empty, hrefs.Select(href => $"<D:href>{Escape(href)}</D:href>"));
        var body = $"""
            <C:addressbook-multiget xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:carddav">
              <D:prop><D:getetag /><C:address-data /></D:prop>
              {hrefXml}
            </C:addressbook-multiget>
            """;
        return ReportResourcesAsync(settings, addressBook, body, cancellationToken);
    }

    public Task<IReadOnlyList<CardDavResourceChange>> QueryAsync(
        CardDavConnectionSettings settings,
        CardDavAddressBook addressBook,
        CancellationToken cancellationToken = default)
    {
        const string body = """
            <C:addressbook-query xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:carddav">
              <D:prop><D:getetag /><C:address-data /></D:prop>
              <C:filter><C:prop-filter name="UID" /></C:filter>
            </C:addressbook-query>
            """;
        return ReportResourcesAsync(settings, addressBook, body, cancellationToken);
    }

    public async Task<CardDavResourceChange> GetResourceAsync(
        CardDavConnectionSettings settings,
        string exactHref,
        CancellationToken cancellationToken = default)
    {
        Validate(settings);
        using var request = new HttpRequestMessage(HttpMethod.Get, exactHref);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/vcard"));
        using var response = await _transport.SendAsync(request, settings.Authentication, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new CardDavResourceChange { ExactHref = exactHref, IsDeleted = true, StatusCode = 404 };
        await _responseHandler.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return new CardDavResourceChange
        {
            ExactHref = response.RequestMessage?.RequestUri?.ToString() ?? exactHref,
            ETag = response.Headers.ETag?.ToString(),
            VCard = await ReadBoundedStringAsync(response.Content, 64L * 1024 * 1024, cancellationToken).ConfigureAwait(false),
            StatusCode = (int)response.StatusCode
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
        using var response = await _transport.SendAsync(request, settings.Authentication, cancellationToken).ConfigureAwait(false);
        await _responseHandler.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return new CardDavWriteResult
        {
            ExactHref = response.Headers.Location is null
                ? response.RequestMessage?.RequestUri?.ToString() ?? exactHref
                : Resolve(response.RequestMessage?.RequestUri ?? new Uri(exactHref), response.Headers.Location.ToString()).ToString(),
            ETag = response.Headers.ETag?.ToString(),
            RequiresRefetch = response.Headers.ETag is null || response.Headers.ETag.IsWeak
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
        using var response = await _transport.SendAsync(request, settings.Authentication, cancellationToken).ConfigureAwait(false);
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
        var body = $"""
            <D:mkcol xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:carddav">
              <D:set><D:prop><D:resourcetype><D:collection /><C:addressbook /></D:resourcetype><D:displayname>{Escape(displayName)}</D:displayname></D:prop></D:set>
            </D:mkcol>
            """;
        using var request = XmlRequest(MkColMethod, target, body, null);
        using var response = await _transport.SendAsync(request, settings.Authentication, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.NotImplemented)
        {
            using var plainMkCol = new HttpRequestMessage(MkColMethod, target);
            using var plainResponse = await _transport.SendAsync(plainMkCol, settings.Authentication, cancellationToken).ConfigureAwait(false);
            await _responseHandler.EnsureSuccessAsync(plainResponse, cancellationToken).ConfigureAwait(false);
            var properties = $"""
                <D:propertyupdate xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:carddav">
                  <D:set><D:prop><D:resourcetype><D:collection /><C:addressbook /></D:resourcetype><D:displayname>{Escape(displayName)}</D:displayname></D:prop></D:set>
                </D:propertyupdate>
                """;
            using var propertyRequest = XmlRequest(PropPatchMethod, target, properties, null);
            using var propertyResponse = await _transport.SendAsync(propertyRequest, settings.Authentication, cancellationToken).ConfigureAwait(false);
            await _responseHandler.EnsureSuccessAsync(propertyResponse, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _responseHandler.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        }
        return new CardDavAddressBook { ExactHref = target.ToString(), DisplayName = displayName, SupportsVCard3 = true };
    }

    public async Task RenameAddressBookAsync(
        CardDavConnectionSettings settings,
        string exactHref,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        Validate(settings);
        var body = $"""
            <D:propertyupdate xmlns:D="DAV:"><D:set><D:prop><D:displayname>{Escape(displayName)}</D:displayname></D:prop></D:set></D:propertyupdate>
            """;
        using var request = XmlRequest(PropPatchMethod, new Uri(exactHref), body, null);
        using var response = await _transport.SendAsync(request, settings.Authentication, cancellationToken).ConfigureAwait(false);
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

    private async Task<IReadOnlyList<CardDavResourceChange>> ReportResourcesAsync(
        CardDavConnectionSettings settings,
        CardDavAddressBook addressBook,
        string body,
        CancellationToken cancellationToken)
    {
        Validate(settings);
        ValidateAddressBook(addressBook);
        var collectionUri = new Uri(addressBook.ExactHref);
        var multistatus = await SendMultistatusAsync(settings, ReportMethod, collectionUri, "1", body, cancellationToken).ConfigureAwait(false);
        return multistatus.Responses.Select(response => ParseResourceChange(response, collectionUri)).ToList();
    }

    private Task<DavMultistatus> PropFindAsync(
        CardDavConnectionSettings settings,
        Uri uri,
        string depth,
        string properties,
        CancellationToken cancellationToken)
        => SendMultistatusAsync(settings, PropFindMethod, uri, depth,
            $"<D:propfind xmlns:D=\"DAV:\" xmlns:C=\"{CardDavNamespace}\" xmlns:CS=\"{CalendarServerNamespace}\"><D:prop>{properties}</D:prop></D:propfind>",
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
        using var response = await _transport.SendAsync(request, settings.Authentication, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.MultiStatus)
        {
            await _responseHandler.EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
            throw new DavRequestException((int)response.StatusCode, "The DAV server returned a non-multistatus response.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await _multistatusReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
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

    private static async Task<string> ReadBoundedStringAsync(
        HttpContent content,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("The DAV response exceeded the configured size limit.");

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maximumBytes)
                throw new InvalidDataException("The DAV response exceeded the configured size limit.");

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(destination.GetBuffer(), 0, checked((int)destination.Length));
    }

    private static CardDavAddressBook ParseAddressBook(DavResponseItem response, Uri baseUri)
    {
        var reports = Property(response, DavNamespace, "supported-report-set")?.Xml;
        var addressData = Property(response, CardDavNamespace, "supported-address-data")?.Xml;
        var privileges = Property(response, DavNamespace, "current-user-privilege-set")?.Xml;
        var extendedMkCol = Property(response, DavNamespace, "supported-method-set")?.Xml;
        return new CardDavAddressBook
        {
            ExactHref = Resolve(baseUri, response.Href).ToString(),
            DisplayName = Property(response, DavNamespace, "displayname")?.Value,
            SyncToken = Property(response, DavNamespace, "sync-token")?.Value,
            CollectionTag = Property(response, CalendarServerNamespace, "getctag")?.Value,
            IsReadOnly = !string.IsNullOrWhiteSpace(privileges) &&
                         !ContainsElement(privileges, DavNamespace, "write") &&
                         !ContainsElement(privileges, DavNamespace, "write-content"),
            SupportsSyncCollection = ContainsElement(reports, DavNamespace, "sync-collection"),
            SupportsMultiget = ContainsElement(reports, CardDavNamespace, "addressbook-multiget"),
            SupportsAddressBookQuery = ContainsElement(reports, CardDavNamespace, "addressbook-query"),
            SupportsVCard3 = string.IsNullOrWhiteSpace(addressData) || addressData.Contains("version=3.0", StringComparison.OrdinalIgnoreCase),
            SupportsVCard4 = addressData?.Contains("version=4.0", StringComparison.OrdinalIgnoreCase) == true,
            SupportsExtendedMkCol = ContainsElement(extendedMkCol, DavNamespace, "extended-mkcol"),
            SupportsAddMember = Property(response, DavNamespace, "add-member") is not null,
            MaximumResourceSize = long.TryParse(Property(response, CardDavNamespace, "max-resource-size")?.Value, out var size) ? size : null
        };
    }

    private static CardDavResourceChange ParseResourceChange(DavResponseItem response, Uri baseUri)
    {
        var status = response.StatusCode ?? response.PropertyStatuses.FirstOrDefault(item => item.StatusCode is >= 400)?.StatusCode ?? 200;
        var successful = response.PropertyStatuses.FirstOrDefault(item => item.StatusCode is >= 200 and < 300);
        return new CardDavResourceChange
        {
            ExactHref = Resolve(baseUri, response.Href).ToString(),
            ETag = Property(successful, DavNamespace, "getetag")?.Value,
            VCard = Property(successful, CardDavNamespace, "address-data")?.Value,
            IsDeleted = status == 404,
            StatusCode = status
        };
    }

    private static DavResponseItem FindSuccessfulResponse(DavMultistatus multistatus, Uri requestUri)
        => multistatus.Responses.FirstOrDefault(response => HrefEquals(response.Href, requestUri, requestUri))
           ?? multistatus.Responses.FirstOrDefault(response => response.PropertyStatuses.Any(status => status.StatusCode is >= 200 and < 300))
           ?? throw new DavRequestException(500, "The DAV response did not contain a successful request resource.");

    private static DavProperty Property(DavResponseItem response, string xmlNamespace, string name)
        => response?.PropertyStatuses.Where(status => status.StatusCode is >= 200 and < 300)
            .SelectMany(status => status.Properties)
            .FirstOrDefault(property => property.Namespace == xmlNamespace && property.Name == name);

    private static DavProperty Property(DavPropertyStatus status, string xmlNamespace, string name)
        => status?.Properties.FirstOrDefault(property => property.Namespace == xmlNamespace && property.Name == name);

    private static bool IsAddressBook(DavProperty property) => ContainsElement(property?.Xml, CardDavNamespace, "addressbook");

    private static bool ContainsElement(string xml, string xmlNamespace, string localName)
    {
        if (string.IsNullOrWhiteSpace(xml)) return false;
        try
        {
            var element = XElement.Parse(xml);
            return element.DescendantsAndSelf().Any(item => item.Name.NamespaceName == xmlNamespace && item.Name.LocalName == localName);
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static string DiscoveryProperties() =>
        "<D:current-user-principal /><C:addressbook-home-set /><D:resourcetype />";

    private static string CollectionProperties() =>
        "<D:resourcetype /><D:displayname /><D:current-user-privilege-set /><D:supported-report-set /><D:supported-method-set /><D:sync-token /><D:add-member /><CS:getctag /><C:supported-address-data /><C:max-resource-size />";

    private static Uri BuildWellKnownUri(string accountAddress)
    {
        var separator = accountAddress?.LastIndexOf('@') ?? -1;
        var domain = separator >= 0 ? accountAddress[(separator + 1)..] : accountAddress;
        if (string.IsNullOrWhiteSpace(domain)) throw new ArgumentException("A CardDAV service URL or account domain is required.");
        return new Uri($"https://{domain}/.well-known/carddav");
    }

    private static Uri Resolve(Uri baseUri, string href)
        => Uri.TryCreate(href, UriKind.Absolute, out var absolute) ? absolute : new Uri(baseUri, href ?? string.Empty);

    private static bool HrefEquals(string href, Uri baseUri, Uri expected)
        => string.Equals(Resolve(baseUri, href).AbsoluteUri, expected.AbsoluteUri, StringComparison.Ordinal);

    private static string EnsureTrailingSlash(string href) => href.EndsWith('/') ? href : href + "/";
    private static string Escape(string value) => SecurityElement.Escape(value ?? string.Empty);

    private static void Validate(CardDavConnectionSettings settings)
    {
        if (settings?.Authentication is null) throw new ArgumentException("CardDAV authentication is required.");
        if (settings.ServiceUri is null && string.IsNullOrWhiteSpace(settings.AccountAddress))
            throw new ArgumentException("A CardDAV service URL or account address is required.");
    }

    private static void ValidateAddressBook(CardDavAddressBook addressBook)
    {
        if (addressBook is null || string.IsNullOrWhiteSpace(addressBook.ExactHref))
            throw new ArgumentException("A CardDAV address-book href is required.");
    }
}
