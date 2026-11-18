#nullable enable
using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Services;

/// <summary>
/// Finds EWS endpoints with anonymous Exchange Autodiscover V2, trying the candidates Outlook tries: the directory's
/// service connection point (domain-joined Windows machines), the DNS SRV record, then the well-known HTTPS hosts.
/// Many organizations publish only the SRV record.
/// </summary>
public sealed class ExchangeAutoDiscoveryService : IExchangeAutoDiscoveryService
{
    private const string AutodiscoverPath = "/autodiscover/autodiscover.json";

    // The keyword Exchange stamps on its Autodiscover service connection points.
    private const string AutodiscoverScpKeyword = "77378F46-2C66-4aa9-A6A6-3E7A48B19596";

    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(6) };
    private static readonly TimeSpan DirectoryTimeout = TimeSpan.FromSeconds(5);

    private readonly IDnsLookupService _dnsLookupService;
    private readonly ILogger _logger = Log.ForContext<ExchangeAutoDiscoveryService>();

    public ExchangeAutoDiscoveryService(IDnsLookupService dnsLookupService)
    {
        _dnsLookupService = dnsLookupService;
    }

    public async Task<string?> TryDiscoverEwsUrlAsync(string emailAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(emailAddress))
            return null;

        var address = emailAddress.Trim();
        var at = address.LastIndexOf('@');
        if (at <= 0 || at == address.Length - 1)
            return null;

        var domain = address[(at + 1)..];

        foreach (var autodiscoverUrl in await BuildCandidateUrlsAsync(domain, cancellationToken).ConfigureAwait(false))
        {
            var ewsUrl = await QueryAutodiscoverV2Async(autodiscoverUrl, address, cancellationToken).ConfigureAwait(false);
            if (ewsUrl is not null)
                return ewsUrl;
        }

        return null;
    }

    private async Task<List<string>> BuildCandidateUrlsAsync(string domain, CancellationToken cancellationToken)
    {
        var candidates = new List<string>();

        if (OperatingSystem.IsWindows())
            candidates.AddRange(await Task.Run(() => OperatingSystem.IsWindows() ? ResolveScpUrls(domain) : [], cancellationToken).ConfigureAwait(false));

        var srvUrl = await ResolveSrvUrlAsync(domain, cancellationToken).ConfigureAwait(false);
        if (srvUrl is not null)
            candidates.Add(srvUrl);

        candidates.Add($"https://autodiscover.{domain}{AutodiscoverPath}");
        candidates.Add($"https://{domain}{AutodiscoverPath}");

        return candidates;
    }

    [SupportedOSPlatform("windows")]
    private List<string> ResolveScpUrls(string emailDomain)
    {
        var urls = new List<string>();

        try
        {
            // A serverless identifier binds to a domain controller of this machine's domain as the signed-in user.
            using var connection = new LdapConnection(new LdapDirectoryIdentifier((string?)null))
            {
                AuthType = AuthType.Negotiate,
                Timeout = DirectoryTimeout
            };
            connection.SessionOptions.ProtocolVersion = 3;
            connection.Bind();

            var rootDseResponse = (SearchResponse)connection.SendRequest(
                new SearchRequest(null, "(objectClass=*)", SearchScope.Base, "configurationNamingContext", "defaultNamingContext"));

            if (rootDseResponse.Entries.Count == 0)
                return urls;

            var rootDse = rootDseResponse.Entries[0];
            var configurationNamingContext = ReadFirstValue(rootDse, "configurationNamingContext");
            var defaultNamingContext = ReadFirstValue(rootDse, "defaultNamingContext");
            if (string.IsNullOrEmpty(configurationNamingContext))
                return urls;

            // A service connection point only answers for mailboxes in this machine's forest.
            if (!IsForestDomain(connection, configurationNamingContext, defaultNamingContext, emailDomain))
                return urls;

            var scpResponse = (SearchResponse)connection.SendRequest(new SearchRequest(
                configurationNamingContext,
                $"(&(objectClass=serviceConnectionPoint)(keywords={AutodiscoverScpKeyword}))",
                SearchScope.Subtree,
                "serviceBindingInformation"));

            foreach (SearchResultEntry entry in scpResponse.Entries)
            {
                var binding = entry.Attributes["serviceBindingInformation"];
                if (binding is null)
                    continue;

                foreach (var value in binding.GetValues(typeof(string)))
                {
                    if (Uri.TryCreate(value as string, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
                        urls.Add(uri.GetLeftPart(UriPartial.Authority) + AutodiscoverPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Exchange Autodiscover directory lookup failed (not domain-joined or no domain controller reachable).");
        }

        return urls;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsForestDomain(LdapConnection connection, string configurationNamingContext, string? defaultNamingContext, string emailDomain)
    {
        var forestDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var directoryDomain = DistinguishedNameToDomain(defaultNamingContext);
        if (directoryDomain is not null)
            forestDomains.Add(directoryDomain);

        try
        {
            var partitionsResponse = (SearchResponse)connection.SendRequest(new SearchRequest(
                "CN=Partitions," + configurationNamingContext, "(objectClass=*)", SearchScope.Base, "uPNSuffixes"));

            if (partitionsResponse.Entries.Count > 0 && partitionsResponse.Entries[0].Attributes["uPNSuffixes"] is { } suffixes)
            {
                foreach (var suffix in suffixes.GetValues(typeof(string)))
                {
                    if (suffix is string value && !string.IsNullOrWhiteSpace(value))
                        forestDomains.Add(value.Trim());
                }
            }
        }
        catch (DirectoryException)
        {
            // Reading UPN suffixes can be denied; the directory's own domain still decides.
        }

        return forestDomains.Contains(emailDomain);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadFirstValue(SearchResultEntry entry, string attributeName)
    {
        var attribute = entry.Attributes[attributeName];
        if (attribute is null || attribute.Count == 0)
            return null;

        return attribute[0] as string;
    }

    private static string? DistinguishedNameToDomain(string? distinguishedName)
    {
        if (string.IsNullOrWhiteSpace(distinguishedName))
            return null;

        var labels = new List<string>();
        foreach (var part in distinguishedName.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
                labels.Add(trimmed[3..]);
        }

        return labels.Count == 0 ? null : string.Join('.', labels);
    }

    private async Task<string?> ResolveSrvUrlAsync(string domain, CancellationToken cancellationToken)
    {
        var records = await _dnsLookupService.GetSrvRecordsAsync($"_autodiscover._tcp.{domain}", cancellationToken).ConfigureAwait(false);

        foreach (var record in records)
        {
            var host = record.Target.TrimEnd('.');
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
                continue;

            var authority = record.Port is 443 or 0 ? host : $"{host}:{record.Port}";
            return $"https://{authority}{AutodiscoverPath}";
        }

        return null;
    }

    private async Task<string?> QueryAutodiscoverV2Async(string autodiscoverUrl, string emailAddress, CancellationToken cancellationToken)
    {
        var url = $"{autodiscoverUrl}?Email={Uri.EscapeDataString(emailAddress)}&Protocol=EWS";

        try
        {
            using var response = await HttpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("Url", out var urlElement) &&
                urlElement.ValueKind == JsonValueKind.String &&
                Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var ewsUri) &&
                ewsUri.Scheme == Uri.UriSchemeHttps)
            {
                // Credentials go to this URL later, so only an HTTPS endpoint is accepted.
                return ewsUri.AbsoluteUri;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Exchange Autodiscover V2 request to {AutodiscoverUrl} failed.", autodiscoverUrl);
        }

        return null;
    }
}
