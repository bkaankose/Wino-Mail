using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Serilog;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models;
using MailCal.Core.Domain.Models.AutoDiscovery;
using MailCal.Core.Domain.Validation;

namespace MailCal.Core.Services;

/// <summary>
/// Mail, CalDAV and CardDAV endpoint discovery that only contacts the user's own mail domain:
/// its autoconfig document, well-known DAV URLs, and common host names checked with the system
/// DNS resolver. No third-party database or DNS-over-HTTPS service is queried.
/// </summary>
public class AutoDiscoveryService : IAutoDiscoveryService
{
    private static readonly ILogger Logger = Log.ForContext<AutoDiscoveryService>();
    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;
    private static readonly HttpMethod OptionsMethod = new("OPTIONS");

    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, Uri> _calDavUriCache = new(IgnoreCase);
    private readonly object _calDavCacheLock = new();

    public AutoDiscoveryService(HttpClient httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    public async Task<AutoDiscoverySettings> GetAutoDiscoverySettings(AutoDiscoveryMinimalSettings autoDiscoveryMinimalSettings)
    {
        if (autoDiscoveryMinimalSettings == null || string.IsNullOrWhiteSpace(autoDiscoveryMinimalSettings.Email))
            return null;

        if (!TryGetEmailParts(autoDiscoveryMinimalSettings.Email, out var localPart, out var domain))
            return null;

        var cancellationToken = CancellationToken.None;
        var incomingProtocol = autoDiscoveryMinimalSettings.IncomingServerType == CustomIncomingServerType.POP3
            ? "POP3"
            : "IMAP";

        var settings = await TryGetThunderbirdSettingsAsync(domain, autoDiscoveryMinimalSettings.Email, localPart, incomingProtocol, cancellationToken).ConfigureAwait(false)
                       ?? await TryGetGuessedHostSettingsAsync(domain, autoDiscoveryMinimalSettings.Email, incomingProtocol, cancellationToken).ConfigureAwait(false);

        if (settings != null && string.IsNullOrWhiteSpace(settings.Domain))
        {
            settings.Domain = domain;
        }

        return settings;
    }

    public Task<Uri> DiscoverCalDavServiceUriAsync(string mailAddress, CancellationToken cancellationToken = default)
        => DiscoverDavServiceUriAsync(DavService.CalDav, mailAddress, cancellationToken);

    public Task<Uri> DiscoverCardDavServiceUriAsync(string mailAddress, CancellationToken cancellationToken = default)
        => DiscoverDavServiceUriAsync(DavService.CardDav, mailAddress, cancellationToken);

    /// <summary>
    /// RFC 6764 discovery limited to the user's own domain: <c>https://domain/.well-known/caldav</c>
    /// (or <c>carddav</c>) and <c>https://caldav.domain/</c>, for the domain and its parent domains.
    /// </summary>
    private async Task<Uri> DiscoverDavServiceUriAsync(DavService service, string mailAddress, CancellationToken cancellationToken)
    {
        if (!TryGetEmailParts(mailAddress, out _, out var domain))
            return null;

        var cacheKey = service.CacheKeyPrefix + domain;

        lock (_calDavCacheLock)
        {
            if (_calDavUriCache.TryGetValue(cacheKey, out var cachedUri))
                return cachedUri;
        }

        var domainCandidates = BuildDomainCandidatesFromHost(domain).ToList();
        var probed = new HashSet<Uri>();

        var resolved = await TryResolveFirstCandidateAsync(GetDavCandidates(service, domainCandidates), probed, cancellationToken).ConfigureAwait(false);

        if (resolved == null)
            return null;

        CacheCalDavUri(cacheKey, resolved);
        return resolved;
    }

    private async Task<Uri> TryResolveFirstCandidateAsync(IEnumerable<Uri> candidates, HashSet<Uri> probed, CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            if (!probed.Add(candidate))
                continue;

            var resolved = await TryResolveCalDavEndpointAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (resolved != null)
                return resolved;
        }

        return null;
    }

    private async Task<AutoDiscoverySettings> TryGetThunderbirdSettingsAsync(
        string lookupDomain,
        string email,
        string localPart,
        string incomingProtocol,
        CancellationToken cancellationToken)
    {
        foreach (var endpoint in BuildThunderbirdEndpoints(lookupDomain, email))
        {
            var settings = await TryGetSettingsFromXmlEndpointAsync(endpoint, email, localPart, lookupDomain, incomingProtocol, cancellationToken).ConfigureAwait(false);
            if (settings != null)
                return settings;
        }

        return null;
    }

    private async Task<AutoDiscoverySettings> TryGetGuessedHostSettingsAsync(
        string domain,
        string email,
        string incomingProtocol,
        CancellationToken cancellationToken)
    {
        var incomingHost = await GetFirstResolvableHostAsync(
            incomingProtocol == "POP3"
                ? [$"pop.{domain}", $"pop3.{domain}", $"mail.{domain}", domain]
                : [$"imap.{domain}", $"mail.{domain}", domain],
            cancellationToken).ConfigureAwait(false);

        var smtpHost = await GetFirstResolvableHostAsync(
            [$"smtp.{domain}", $"mail.{domain}", domain],
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(incomingHost) || string.IsNullOrWhiteSpace(smtpHost))
            return null;

        return new AutoDiscoverySettings
        {
            Domain = domain,
            Settings =
            [
                new AutoDiscoveryProviderSetting
                {
                    Protocol = incomingProtocol,
                    Address = incomingHost,
                    Port = incomingProtocol == "POP3" ? 995 : 993,
                    Secure = "SSL",
                    Username = email
                },
                new AutoDiscoveryProviderSetting
                {
                    Protocol = "SMTP",
                    Address = smtpHost,
                    Port = 587,
                    Secure = "STARTTLS",
                    Username = email
                }
            ]
        };
    }

    private async Task<AutoDiscoverySettings> TryGetSettingsFromXmlEndpointAsync(
        string endpoint,
        string email,
        string localPart,
        string domain,
        string incomingProtocol,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseThunderbirdSettings(content, email, localPart, domain, incomingProtocol);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to read autodiscovery XML endpoint {Endpoint}", endpoint);
            return null;
        }
    }

    private static AutoDiscoverySettings ParseThunderbirdSettings(string xmlContent, string email, string localPart, string domain, string incomingProtocol)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
            return null;

        try
        {
            var document = XDocument.Parse(xmlContent);

            var incomingServers = document
                .Descendants()
                .Where(e => e.Name.LocalName == "incomingServer")
                .Where(e => string.Equals((string)e.Attribute("type"), incomingProtocol, StringComparison.OrdinalIgnoreCase))
                .Select(e => ParseThunderbirdServer(e, incomingProtocol, email, localPart, domain))
                .Where(e => e != null)
                .ToList();

            var outgoingServers = document
                .Descendants()
                .Where(e => e.Name.LocalName == "outgoingServer")
                .Where(e => string.Equals((string)e.Attribute("type"), "smtp", StringComparison.OrdinalIgnoreCase))
                .Select(e => ParseThunderbirdServer(e, "SMTP", email, localPart, domain))
                .Where(e => e != null)
                .ToList();

            var bestIncoming = SelectBestServerSetting(incomingServers);
            var bestOutgoing = SelectBestServerSetting(outgoingServers);

            if (bestIncoming == null || bestOutgoing == null)
                return null;

            return new AutoDiscoverySettings
            {
                Domain = domain,
                Settings = [bestIncoming, bestOutgoing]
            };
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to parse Thunderbird autodiscovery XML.");
            return null;
        }
    }

    private static AutoDiscoveryProviderSetting ParseThunderbirdServer(
        XElement serverElement,
        string protocol,
        string email,
        string localPart,
        string domain)
    {
        var address = ResolveTemplate(GetElementValue(serverElement, "hostname"), email, localPart, domain);
        var username = ResolveTemplate(GetElementValue(serverElement, "username"), email, localPart, domain);
        var socketType = ResolveTemplate(GetElementValue(serverElement, "socketType"), email, localPart, domain);
        var authenticationMethods = serverElement.Elements()
            .Where(element => element.Name.LocalName == "authentication")
            .Select(element => ResolveTemplate(element.Value, email, localPart, domain)?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        if (string.IsNullOrWhiteSpace(address))
            return null;

        if (!int.TryParse(GetElementValue(serverElement, "port"), out var port))
            return null;

        return new AutoDiscoveryProviderSetting
        {
            Protocol = protocol,
            Address = address.Trim(),
            Port = port,
            Secure = socketType?.Trim() ?? string.Empty,
            Username = string.IsNullOrWhiteSpace(username) ? email : username.Trim(),
            AuthenticationMethods = authenticationMethods
        };
    }

    private static AutoDiscoveryProviderSetting SelectBestServerSetting(IReadOnlyCollection<AutoDiscoveryProviderSetting> settings)
    {
        if (settings == null || settings.Count == 0)
            return null;

        return settings
            .OrderByDescending(GetSecurityScore)
            .ThenBy(s => s.Port)
            .FirstOrDefault();
    }

    private static int GetSecurityScore(AutoDiscoveryProviderSetting setting)
    {
        if (setting == null)
            return 0;

        var secureValue = setting.Secure ?? string.Empty;

        if (secureValue.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase))
            return 2;

        if (secureValue.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
            secureValue.Contains("TLS", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        return 1;
    }

    private static string GetElementValue(XElement element, string localName)
        => element.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    private static string ResolveTemplate(string value, string email, string localPart, string domain)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        return value
            .Replace("%EMAILADDRESS%", email, StringComparison.OrdinalIgnoreCase)
            .Replace("%EMAILLOCALPART%", localPart, StringComparison.OrdinalIgnoreCase)
            .Replace("%EMAILDOMAIN%", domain, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> BuildThunderbirdEndpoints(string domain, string email)
    {
        var escapedEmail = Uri.EscapeDataString(email);
        yield return $"https://autoconfig.{domain}/mail/config-v1.1.xml?emailaddress={escapedEmail}";
        yield return $"https://{domain}/.well-known/autoconfig/mail/config-v1.1.xml?emailaddress={escapedEmail}";
    }

    private async Task<string> GetFirstResolvableHostAsync(IEnumerable<string> hostCandidates, CancellationToken cancellationToken)
    {
        foreach (var host in hostCandidates.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(IgnoreCase))
        {
            if (await HasAnyDnsAddressRecordAsync(host, cancellationToken).ConfigureAwait(false))
                return host;
        }

        return null;
    }

    private async Task<bool> HasAnyDnsAddressRecordAsync(string host, CancellationToken cancellationToken)
    {
        if (MailAccountAddressValidator.IsImplicitlyResolvableHost(host))
            return true;

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            return addresses.Length > 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "DNS lookup failed for {Host}", host);
            return false;
        }
    }

    private async Task<Uri> TryResolveCalDavEndpointAsync(Uri candidate, CancellationToken cancellationToken)
    {
        var getResult = await ProbeCalDavEndpointAsync(candidate, HttpMethod.Get, cancellationToken).ConfigureAwait(false);
        if (getResult != null)
            return getResult;

        return await ProbeCalDavEndpointAsync(candidate, OptionsMethod, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Uri> ProbeCalDavEndpointAsync(Uri uri, HttpMethod method, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, uri);
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (TryResolveRedirectTarget(uri, response, out var redirectTarget))
                return redirectTarget;

            if (!IsPossibleCalDavEndpoint(response))
                return null;

            return response.RequestMessage?.RequestUri ?? uri;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "CalDAV probe failed for {Uri} with method {Method}", uri, method);
            return null;
        }
    }

    private static bool IsPossibleCalDavEndpoint(HttpResponseMessage response)
    {
        if (response == null)
            return false;

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.MultiStatus)
            return true;

        var hasDavHeader = response.Headers.Contains("DAV");
        var hasDavMethod = response.Headers.TryGetValues("Allow", out var allowValues)
                           && allowValues.Any(value =>
                               value.Contains("PROPFIND", StringComparison.OrdinalIgnoreCase) ||
                               value.Contains("REPORT", StringComparison.OrdinalIgnoreCase));

        if (response.StatusCode == HttpStatusCode.MethodNotAllowed)
            return hasDavHeader || hasDavMethod;

        return response.IsSuccessStatusCode && (hasDavHeader || hasDavMethod);
    }

    private static bool TryResolveRedirectTarget(Uri baseUri, HttpResponseMessage response, out Uri resolvedUri)
    {
        resolvedUri = null;

        if (response == null || !IsRedirectStatusCode(response.StatusCode))
            return false;

        if (response.Headers.Location == null)
            return false;

        resolvedUri = MailCal.Services.Dav.DavUri.Resolve(baseUri, response.Headers.Location);

        return true;
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.MovedPermanently
           || statusCode == HttpStatusCode.Found
           || statusCode == HttpStatusCode.RedirectMethod
           || statusCode == HttpStatusCode.TemporaryRedirect
           || (int)statusCode == 308;

    private static IEnumerable<Uri> GetDavCandidates(DavService service, IEnumerable<string> domains)
    {
        foreach (var candidateDomain in domains)
        {
            yield return new Uri($"https://{candidateDomain}{service.WellKnownPath}");
            yield return new Uri($"https://{service.HostPrefix}{candidateDomain}/");
        }
    }

    /// <summary>
    /// Returns the host and each parent domain down to the registrable one, nearest first.
    /// <c>mx1.mail.example.net</c> gives <c>mx1.mail.example.net</c>, <c>mail.example.net</c> and <c>example.net</c>;
    /// hosters publish their ISPDB entry under the registrable domain, as Thunderbird looks it up.
    /// A two-label public suffix such as <c>co.uk</c> is never returned.
    /// </summary>
    internal static IEnumerable<string> BuildDomainCandidatesFromHost(string hostOrDomain)
    {
        if (string.IsNullOrWhiteSpace(hostOrDomain))
            yield break;

        var normalized = hostOrDomain.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        yield return normalized;

        var segments = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);

        for (var skip = 1; segments.Length - skip >= 2; skip++)
        {
            var remaining = segments.Skip(skip).ToArray();

            if (remaining.Length == 2 && IsLikelyPublicSecondLevelDomain(remaining[0], remaining[1]))
                yield break;

            yield return string.Join('.', remaining);
        }
    }

    private static readonly HashSet<string> PublicSecondLevelLabels = new(IgnoreCase)
    {
        "ac", "co", "com", "edu", "gob", "gov", "gv", "ltd", "me", "mil", "ne", "net", "nic", "or", "org", "plc", "sch"
    };

    /// <summary>Heuristic for country-code second levels such as <c>co.uk</c>, <c>com.tr</c> or <c>or.jp</c>.</summary>
    private static bool IsLikelyPublicSecondLevelDomain(string secondLevel, string topLevel)
        => topLevel.Length == 2 && PublicSecondLevelLabels.Contains(secondLevel);

    private static bool TryGetEmailParts(string email, out string localPart, out string domain)
    {
        localPart = null;
        domain = null;

        if (string.IsNullOrWhiteSpace(email))
            return false;

        var separatorIndex = email.IndexOf('@');
        if (separatorIndex <= 0 || separatorIndex >= email.Length - 1)
            return false;

        localPart = email[..separatorIndex];
        domain = email[(separatorIndex + 1)..];
        return !string.IsNullOrWhiteSpace(localPart) && !string.IsNullOrWhiteSpace(domain);
    }

    private void CacheCalDavUri(string cacheKey, Uri serviceUri)
    {
        lock (_calDavCacheLock)
        {
            _calDavUriCache[cacheKey] = serviceUri;
        }
    }

    /// <summary>The names RFC 6764 gives each DAV service.</summary>
    private sealed record DavService(string Name, string SecureSrvLabel, string PlainSrvLabel, string WellKnownPath, string HostPrefix, string CacheKeyPrefix)
    {
        public static readonly DavService CalDav = new("CalDAV", "_caldavs._tcp", "_caldav._tcp", "/.well-known/caldav", "caldav.", string.Empty);
        public static readonly DavService CardDav = new("CardDAV", "_carddavs._tcp", "_carddav._tcp", "/.well-known/carddav", "carddav.", "carddav:");
    }
}
