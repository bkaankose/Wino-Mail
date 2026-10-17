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
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models;
using Wino.Core.Domain.Models.AutoDiscovery;
using Wino.Core.Domain.Validation;

namespace Wino.Core.Services;

/// <summary>
/// Mail and CalDAV endpoint discovery with Thunderbird-style methods and fallbacks.
/// </summary>
public class AutoDiscoveryService : IAutoDiscoveryService
{
    private const string ThunderbirdIspdbUrl = "https://autoconfig.thunderbird.net/v1.1/";
    private const string FiretrustUrl = "https://emailsettings.firetrust.com/settings?q=";
    private const string GoogleDnsResolveUrl = "https://dns.google/resolve";

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
                       ?? await TryGetIspdbSettingsAsync(domain, autoDiscoveryMinimalSettings.Email, localPart, incomingProtocol, cancellationToken).ConfigureAwait(false)
                       ?? await TryGetMxBasedSettingsAsync(domain, autoDiscoveryMinimalSettings.Email, localPart, incomingProtocol, cancellationToken).ConfigureAwait(false)
                       ?? await TryGetSrvBasedSettingsAsync(domain, autoDiscoveryMinimalSettings.Email, incomingProtocol, cancellationToken).ConfigureAwait(false)
                       ?? await TryGetGuessedHostSettingsAsync(domain, autoDiscoveryMinimalSettings.Email, incomingProtocol, cancellationToken).ConfigureAwait(false)
                       ?? (incomingProtocol == "IMAP"
                           ? await GetSettingsFromFiretrustAsync(autoDiscoveryMinimalSettings.Email, cancellationToken).ConfigureAwait(false)
                           : null);

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
    /// RFC 6764 discovery. Candidates are probed in this order, and the first one that answers like
    /// a DAV endpoint wins:
    /// 1. <c>_caldavs._tcp</c> SRV targets of the domain and its parent (TLS), with the TXT <c>path</c>.
    /// 2. HTTPS on the targets of a plain <c>_caldav._tcp</c> record, when no TLS record exists.
    /// 3. <c>https://domain/.well-known/caldav</c> and <c>https://caldav.domain/</c>.
    /// 4. TLS SRV records of the domains the MX hosts belong to (hosted mail).
    /// 5. The well-known URI on the hoster's groupware host, read from the <c>_autodiscover._tcp</c> SRV target
    ///    and the <c>autodiscover</c> and <c>autoconfig</c> CNAME targets (hosted groupware, for example).
    /// 6. Plain HTTP on the <c>_caldav._tcp</c> targets, only when nothing secure answered.
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
        var insecureCandidates = new List<Uri>();
        var probed = new HashSet<Uri>();

        var resolved = await TryResolveSrvCandidatesAsync(service, domainCandidates, insecureCandidates, probed, cancellationToken).ConfigureAwait(false)
                       ?? await TryResolveFirstCandidateAsync(GetDavCandidates(service, domainCandidates), probed, cancellationToken).ConfigureAwait(false);

        if (resolved == null)
        {
            var mxDomains = (await GetMxSearchDomainsAsync(domain, cancellationToken).ConfigureAwait(false))
                .Where(candidate => !domainCandidates.Contains(candidate, IgnoreCase))
                .ToList();

            if (mxDomains.Count > 0)
            {
                resolved = await TryResolveSrvCandidatesAsync(service, mxDomains, insecureCandidates, probed, cancellationToken).ConfigureAwait(false);
            }
        }

        if (resolved == null)
        {
            var hostedCandidates = (await GetAutoconfigHostsAsync(domain, cancellationToken).ConfigureAwait(false))
                .Select(host => new Uri($"https://{host}{service.WellKnownPath}"));

            resolved = await TryResolveFirstCandidateAsync(hostedCandidates, probed, cancellationToken).ConfigureAwait(false);
        }

        if (resolved == null && insecureCandidates.Count > 0)
        {
            Logger.Warning("{Service} discovery for {Domain} found only unencrypted SRV records; probing plain HTTP", service.Name, domain);
            resolved = await TryResolveFirstCandidateAsync(insecureCandidates, probed, cancellationToken).ConfigureAwait(false);
        }

        if (resolved == null)
            return null;

        CacheCalDavUri(cacheKey, resolved);
        return resolved;
    }

    /// <summary>
    /// Probes the TLS SRV targets of each domain in order. A domain without a TLS record has its plain
    /// record read instead: the target is tried over HTTPS first, and the HTTP form is kept in
    /// <paramref name="insecureCandidates"/> as a last resort.
    /// </summary>
    private async Task<Uri> TryResolveSrvCandidatesAsync(
        DavService service,
        IReadOnlyList<string> domains,
        List<Uri> insecureCandidates,
        HashSet<Uri> probed,
        CancellationToken cancellationToken)
    {
        foreach (var candidateDomain in domains)
        {
            var secureCandidates = await GetSrvServiceUrisAsync(service, $"{service.SecureSrvLabel}.{candidateDomain}", "https", cancellationToken).ConfigureAwait(false);

            if (secureCandidates.Count == 0)
            {
                var plainCandidates = await GetSrvServiceUrisAsync(service, $"{service.PlainSrvLabel}.{candidateDomain}", "http", cancellationToken).ConfigureAwait(false);

                foreach (var plainCandidate in plainCandidates)
                {
                    secureCandidates.Add(UpgradeToHttps(plainCandidate));
                    insecureCandidates.Add(plainCandidate);
                }
            }

            var resolved = await TryResolveFirstCandidateAsync(secureCandidates, probed, cancellationToken).ConfigureAwait(false);
            if (resolved != null)
                return resolved;
        }

        return null;
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

    /// <summary>
    /// Reads an RFC 6764 SRV record and its TXT <c>path</c> into service URIs, best target first.
    /// RFC 2782: lower priority wins, then higher weight. A "." target (no service) does not parse,
    /// so it yields no candidate. Without a TXT path the target's well-known URI is used (RFC 6764 section 6).
    /// </summary>
    private async Task<List<Uri>> GetSrvServiceUrisAsync(DavService service, string queryName, string scheme, CancellationToken cancellationToken)
    {
        var records = (await QueryDnsAsync(queryName, "SRV", cancellationToken).ConfigureAwait(false))
            .Select(ParseSrvRecord)
            .Where(record => record != null)
            .OrderBy(record => record.Priority)
            .ThenByDescending(record => record.Weight)
            .ToList();

        var uris = new List<Uri>();

        if (records.Count == 0)
            return uris;

        var txtRecords = await QueryDnsAsync(queryName, "TXT", cancellationToken).ConfigureAwait(false);
        var path = ParseTxtPath(txtRecords) ?? service.WellKnownPath;

        foreach (var record in records)
        {
            if (record.Port is <= 0 or > 65535)
                continue;

            var defaultPort = scheme == "https" ? 443 : 80;
            var authority = record.Port == defaultPort ? record.Target : $"{record.Target}:{record.Port}";

            if (Uri.TryCreate($"{scheme}://{authority}{path}", UriKind.Absolute, out var uri) && !uris.Contains(uri))
                uris.Add(uri);
        }

        Logger.Debug("{Service} SRV {QueryName} gave {Count} candidate(s)", service.Name, queryName, uris.Count);
        return uris;
    }

    /// <summary>
    /// Returns the context path from TXT strings such as <c>path=/caldav/</c> (RFC 6764 section 4),
    /// or null when no usable path is published.
    /// </summary>
    internal static string ParseTxtPath(IEnumerable<string> txtRecords)
    {
        foreach (var record in txtRecords ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(record))
                continue;

            // DNS-over-HTTPS returns character strings quoted or unquoted, possibly several per record.
            var segments = record.Split("\" \"", StringSplitOptions.None)
                .Select(segment => segment.Trim().Trim('"').Trim());

            foreach (var segment in segments)
            {
                if (!segment.StartsWith("path=", StringComparison.OrdinalIgnoreCase))
                    continue;

                var path = segment["path=".Length..].Trim();
                if (path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal))
                    return path;
            }
        }

        return null;
    }

    private static Uri UpgradeToHttps(Uri plainUri)
    {
        var builder = new UriBuilder(plainUri)
        {
            Scheme = Uri.UriSchemeHttps,
            Port = plainUri.IsDefaultPort ? -1 : plainUri.Port
        };

        return builder.Uri;
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

    private async Task<AutoDiscoverySettings> TryGetIspdbSettingsAsync(
        string lookupDomain,
        string email,
        string localPart,
        string incomingProtocol,
        CancellationToken cancellationToken)
    {
        var endpoint = $"{ThunderbirdIspdbUrl}{lookupDomain}?emailaddress={Uri.EscapeDataString(email)}";
        return await TryGetSettingsFromXmlEndpointAsync(endpoint, email, localPart, lookupDomain, incomingProtocol, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AutoDiscoverySettings> TryGetMxBasedSettingsAsync(
        string domain,
        string email,
        string localPart,
        string incomingProtocol,
        CancellationToken cancellationToken)
    {
        var mxDomains = await GetMxSearchDomainsAsync(domain, cancellationToken).ConfigureAwait(false);

        foreach (var mxDomain in mxDomains)
        {
            if (IgnoreCase.Equals(mxDomain, domain))
                continue;

            var settings = await TryGetThunderbirdSettingsAsync(mxDomain, email, localPart, incomingProtocol, cancellationToken).ConfigureAwait(false)
                           ?? await TryGetIspdbSettingsAsync(mxDomain, email, localPart, incomingProtocol, cancellationToken).ConfigureAwait(false);

            if (settings != null)
                return settings;
        }

        return null;
    }

    private async Task<AutoDiscoverySettings> TryGetSrvBasedSettingsAsync(
        string domain,
        string email,
        string incomingProtocol,
        CancellationToken cancellationToken)
    {
        var incoming = incomingProtocol == "POP3"
            ? await TryResolveSrvRecordAsync($"_pop3s._tcp.{domain}", "POP3", "SSL", cancellationToken).ConfigureAwait(false)
              ?? await TryResolveSrvRecordAsync($"_pop3._tcp.{domain}", "POP3", "STARTTLS", cancellationToken).ConfigureAwait(false)
            : await TryResolveSrvRecordAsync($"_imaps._tcp.{domain}", "IMAP", "SSL", cancellationToken).ConfigureAwait(false)
              ?? await TryResolveSrvRecordAsync($"_imap._tcp.{domain}", "IMAP", "STARTTLS", cancellationToken).ConfigureAwait(false);

        var outgoing = await TryResolveSrvRecordAsync($"_submissions._tcp.{domain}", "SMTP", "SSL", cancellationToken).ConfigureAwait(false)
                      ?? await TryResolveSrvRecordAsync($"_submission._tcp.{domain}", "SMTP", "STARTTLS", cancellationToken).ConfigureAwait(false)
                      ?? await TryResolveSrvRecordAsync($"_smtp._tcp.{domain}", "SMTP", "STARTTLS", cancellationToken).ConfigureAwait(false);

        if (incoming == null || outgoing == null)
            return null;

        incoming.Username = email;
        outgoing.Username = email;

        return new AutoDiscoverySettings
        {
            Domain = domain,
            Settings = [incoming, outgoing]
        };
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

    private async Task<AutoDiscoverySettings> GetSettingsFromFiretrustAsync(string mailAddress, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{FiretrustUrl}{Uri.EscapeDataString(mailAddress)}", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warning("Firetrust autodiscovery failed with status {StatusCode}", response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize(content, DomainModelsJsonContext.Default.AutoDiscoverySettings);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to deserialize Firetrust autodiscovery response.");
            return null;
        }
    }

    private async Task<AutoDiscoveryProviderSetting> TryResolveSrvRecordAsync(
        string queryName,
        string protocol,
        string secureHint,
        CancellationToken cancellationToken)
    {
        var records = await QueryDnsAsync(queryName, "SRV", cancellationToken).ConfigureAwait(false);
        var srvRecord = records
            .Select(ParseSrvRecord)
            .Where(r => r != null)
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Weight)
            .FirstOrDefault();

        if (srvRecord == null)
            return null;

        return new AutoDiscoveryProviderSetting
        {
            Protocol = protocol,
            Address = srvRecord.Target,
            Port = srvRecord.Port,
            Secure = secureHint
        };
    }

    private async Task<IReadOnlyList<string>> GetMxSearchDomainsAsync(string domain, CancellationToken cancellationToken)
    {
        var results = new List<string> { domain };
        var records = await QueryDnsAsync(domain, "MX", cancellationToken).ConfigureAwait(false);

        var hosts = records
            .Select(ParseMxRecord)
            .Where(r => r != null)
            .OrderBy(r => r.Preference)
            .Select(r => r.Target)
            .Distinct(IgnoreCase)
            .ToList();

        foreach (var host in hosts)
        {
            foreach (var candidateDomain in BuildDomainCandidatesFromHost(host))
            {
                if (!results.Contains(candidateDomain, IgnoreCase))
                {
                    results.Add(candidateDomain);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Hosts that a domain delegates client configuration to: the <c>_autodiscover._tcp</c> SRV target and the
    /// CNAME targets of <c>autodiscover.domain</c> and <c>autoconfig.domain</c>. Hosted groupware usually serves
    /// DAV from the same host.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetAutoconfigHostsAsync(string domain, CancellationToken cancellationToken)
    {
        var hosts = new List<string>();

        var srvTargets = (await QueryDnsAsync($"_autodiscover._tcp.{domain}", "SRV", cancellationToken).ConfigureAwait(false))
            .Select(ParseSrvRecord)
            .Where(record => record != null)
            .OrderBy(record => record.Priority)
            .ThenByDescending(record => record.Weight)
            .Select(record => record.Target);

        hosts.AddRange(srvTargets);

        foreach (var alias in new[] { $"autodiscover.{domain}", $"autoconfig.{domain}" })
        {
            var targets = await QueryDnsAsync(alias, "CNAME", cancellationToken).ConfigureAwait(false);
            hosts.AddRange(targets.Select(target => target.Trim().TrimEnd('.')));
        }

        return hosts
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Distinct(IgnoreCase)
            .ToList();
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

        var aRecords = await QueryDnsAsync(host, "A", cancellationToken).ConfigureAwait(false);
        if (aRecords.Count > 0)
            return true;

        var aaaaRecords = await QueryDnsAsync(host, "AAAA", cancellationToken).ConfigureAwait(false);
        return aaaaRecords.Count > 0;
    }

    private async Task<IReadOnlyList<string>> QueryDnsAsync(string queryName, string queryType, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{GoogleDnsResolveUrl}?name={Uri.EscapeDataString(queryName)}&type={Uri.EscapeDataString(queryType)}";
            using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Array.Empty<string>();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("Answer", out var answerArray) ||
                answerArray.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var values = new List<string>();

            foreach (var answer in answerArray.EnumerateArray())
            {
                if (answer.TryGetProperty("data", out var dataNode) && dataNode.ValueKind == JsonValueKind.String)
                {
                    var data = dataNode.GetString();
                    if (!string.IsNullOrWhiteSpace(data))
                        values.Add(data);
                }
            }

            return values;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "DNS-over-HTTPS query failed for {QueryName} ({Type})", queryName, queryType);
            return Array.Empty<string>();
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

        resolvedUri = Wino.Services.Dav.DavUri.Resolve(baseUri, response.Headers.Location);

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

    private static SrvRecord ParseSrvRecord(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return null;

        var parts = rawValue.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
            return null;

        if (!ushort.TryParse(parts[0], out var priority) ||
            !ushort.TryParse(parts[1], out var weight) ||
            !int.TryParse(parts[2], out var port))
        {
            return null;
        }

        var target = parts[3].Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(target))
            return null;

        return new SrvRecord(priority, weight, port, target);
    }

    private static MxRecord ParseMxRecord(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return null;

        var parts = rawValue.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !ushort.TryParse(parts[0], out var preference))
            return null;

        var target = parts[1].Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(target))
            return null;

        return new MxRecord(preference, target);
    }

    private sealed record SrvRecord(ushort Priority, ushort Weight, int Port, string Target);

    /// <summary>The names RFC 6764 gives each DAV service.</summary>
    private sealed record DavService(string Name, string SecureSrvLabel, string PlainSrvLabel, string WellKnownPath, string HostPrefix, string CacheKeyPrefix)
    {
        public static readonly DavService CalDav = new("CalDAV", "_caldavs._tcp", "_caldav._tcp", "/.well-known/caldav", "caldav.", string.Empty);
        public static readonly DavService CardDav = new("CardDAV", "_carddavs._tcp", "_carddav._tcp", "/.well-known/carddav", "carddav.", "carddav:");
    }
    private sealed record MxRecord(ushort Preference, string Target);
}
