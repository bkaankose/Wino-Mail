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

        var settings = await TryGetThunderbirdSettingsAsync(domain, autoDiscoveryMinimalSettings.Email, localPart, cancellationToken).ConfigureAwait(false)
                       ?? await TryGetIspdbSettingsAsync(domain, autoDiscoveryMinimalSettings.Email, localPart, cancellationToken).ConfigureAwait(false)
                       ?? await TryGetMxBasedSettingsAsync(domain, autoDiscoveryMinimalSettings.Email, localPart, cancellationToken).ConfigureAwait(false)
                       ?? await TryGetSrvBasedSettingsAsync(domain, autoDiscoveryMinimalSettings.Email, cancellationToken).ConfigureAwait(false)
                       ?? await TryGetGuessedHostSettingsAsync(domain, autoDiscoveryMinimalSettings.Email, cancellationToken).ConfigureAwait(false)
                       ?? await GetSettingsFromFiretrustAsync(autoDiscoveryMinimalSettings.Email, cancellationToken).ConfigureAwait(false);

        if (settings != null && string.IsNullOrWhiteSpace(settings.Domain))
        {
            settings.Domain = domain;
        }

        return settings;
    }

    public async Task<Uri> DiscoverCalDavServiceUriAsync(string mailAddress, CancellationToken cancellationToken = default)
    {
        if (!TryGetEmailParts(mailAddress, out _, out var domain))
            return null;

        lock (_calDavCacheLock)
        {
            if (_calDavUriCache.TryGetValue(domain, out var cachedUri))
                return cachedUri;
        }

        var knownProviderUri = TryGetKnownProviderCalDavUri(domain);
        if (knownProviderUri != null)
        {
            CacheCalDavUri(domain, knownProviderUri);
            return knownProviderUri;
        }

        foreach (var candidate in GetCalDavCandidates(domain))
        {
            var resolved = await TryResolveCalDavEndpointAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (resolved == null)
                continue;

            CacheCalDavUri(domain, resolved);
            return resolved;
        }

        return null;
    }

    private async Task<AutoDiscoverySettings> TryGetThunderbirdSettingsAsync(
        string lookupDomain,
        string email,
        string localPart,
        CancellationToken cancellationToken)
    {
        foreach (var endpoint in BuildThunderbirdEndpoints(lookupDomain, email))
        {
            var settings = await TryGetSettingsFromXmlEndpointAsync(endpoint, email, localPart, lookupDomain, cancellationToken).ConfigureAwait(false);
            if (settings != null)
                return settings;
        }

        return null;
    }

    private async Task<AutoDiscoverySettings> TryGetIspdbSettingsAsync(
        string lookupDomain,
        string email,
        string localPart,
        CancellationToken cancellationToken)
    {
        var endpoint = $"{ThunderbirdIspdbUrl}{lookupDomain}?emailaddress={Uri.EscapeDataString(email)}";
        return await TryGetSettingsFromXmlEndpointAsync(endpoint, email, localPart, lookupDomain, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AutoDiscoverySettings> TryGetMxBasedSettingsAsync(
        string domain,
        string email,
        string localPart,
        CancellationToken cancellationToken)
    {
        var mxDomains = await GetMxSearchDomainsAsync(domain, cancellationToken).ConfigureAwait(false);

        foreach (var mxDomain in mxDomains)
        {
            if (IgnoreCase.Equals(mxDomain, domain))
                continue;

            var settings = await TryGetThunderbirdSettingsAsync(mxDomain, email, localPart, cancellationToken).ConfigureAwait(false)
                           ?? await TryGetIspdbSettingsAsync(mxDomain, email, localPart, cancellationToken).ConfigureAwait(false);

            if (settings != null)
                return settings;
        }

        return null;
    }

    private async Task<AutoDiscoverySettings> TryGetSrvBasedSettingsAsync(
        string domain,
        string email,
        CancellationToken cancellationToken)
    {
        var incoming = await TryResolveSrvRecordAsync($"_imaps._tcp.{domain}", "IMAP", "SSL", cancellationToken).ConfigureAwait(false)
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
        CancellationToken cancellationToken)
    {
        var imapHost = await GetFirstResolvableHostAsync(
            [$"imap.{domain}", $"mail.{domain}", domain],
            cancellationToken).ConfigureAwait(false);

        var smtpHost = await GetFirstResolvableHostAsync(
            [$"smtp.{domain}", $"mail.{domain}", domain],
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(imapHost) || string.IsNullOrWhiteSpace(smtpHost))
            return null;

        return new AutoDiscoverySettings
        {
            Domain = domain,
            Settings =
            [
                new AutoDiscoveryProviderSetting
                {
                    Protocol = "IMAP",
                    Address = imapHost,
                    Port = 993,
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
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseThunderbirdSettings(content, email, localPart, domain);
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

    private static AutoDiscoverySettings ParseThunderbirdSettings(string xmlContent, string email, string localPart, string domain)
    {
        if (string.IsNullOrWhiteSpace(xmlContent))
            return null;

        try
        {
            var document = XDocument.Parse(xmlContent);

            var incomingServers = document
                .Descendants()
                .Where(e => e.Name.LocalName == "incomingServer")
                .Where(e => string.Equals((string)e.Attribute("type"), "imap", StringComparison.OrdinalIgnoreCase))
                .Select(e => ParseThunderbirdServer(e, "IMAP", email, localPart, domain))
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
            Username = string.IsNullOrWhiteSpace(username) ? email : username.Trim()
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

        if (secureValue.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
            secureValue.Contains("TLS", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (secureValue.Contains("STARTTLS", StringComparison.OrdinalIgnoreCase))
            return 2;

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

        resolvedUri = response.Headers.Location.IsAbsoluteUri
            ? response.Headers.Location
            : new Uri(baseUri, response.Headers.Location);

        return true;
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode)
        => statusCode == HttpStatusCode.MovedPermanently
           || statusCode == HttpStatusCode.Found
           || statusCode == HttpStatusCode.RedirectMethod
           || statusCode == HttpStatusCode.TemporaryRedirect
           || (int)statusCode == 308;

    private static Uri TryGetKnownProviderCalDavUri(string domain)
    {
        if (domain.EndsWith("icloud.com", StringComparison.OrdinalIgnoreCase) ||
            domain.EndsWith("me.com", StringComparison.OrdinalIgnoreCase) ||
            domain.EndsWith("mac.com", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri("https://caldav.icloud.com/");
        }

        if (domain.Contains("yahoo.", StringComparison.OrdinalIgnoreCase) ||
            domain.EndsWith("aol.com", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri("https://caldav.calendar.yahoo.com/");
        }

        return null;
    }

    private static IEnumerable<Uri> GetCalDavCandidates(string domain)
    {
        foreach (var candidateDomain in BuildDomainCandidatesFromHost(domain))
        {
            yield return new Uri($"https://{candidateDomain}/.well-known/caldav");
            yield return new Uri($"https://caldav.{candidateDomain}/");
        }
    }

    private static IEnumerable<string> BuildDomainCandidatesFromHost(string hostOrDomain)
    {
        if (string.IsNullOrWhiteSpace(hostOrDomain))
            yield break;

        var normalized = hostOrDomain.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(normalized))
            yield break;

        yield return normalized;

        var segments = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 2)
        {
            yield return string.Join('.', segments.Skip(1));
        }
    }

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

    private void CacheCalDavUri(string domain, Uri calDavUri)
    {
        lock (_calDavCacheLock)
        {
            _calDavUriCache[domain] = calDavUri;
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
    private sealed record MxRecord(ushort Preference, string Target);
}
