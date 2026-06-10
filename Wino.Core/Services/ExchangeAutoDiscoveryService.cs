using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DnsClient;
using DnsClient.Protocol;
using Serilog;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Services;

/// <summary>Discovers EWS endpoints with Exchange Autodiscover V2.</summary>
public sealed class ExchangeAutoDiscoveryService : IExchangeAutoDiscoveryService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(6) };
    private readonly ILogger _logger = Log.ForContext<ExchangeAutoDiscoveryService>();

    public async Task<string> TryDiscoverEwsUrlAsync(string emailAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(emailAddress) || !emailAddress.Contains('@'))
            return null;

        var domain = emailAddress.Split('@')[^1].Trim();
        if (string.IsNullOrWhiteSpace(domain))
            return null;

        foreach (var autodiscoverUrl in await BuildCandidateAutodiscoverUrlsAsync(domain, cancellationToken).ConfigureAwait(false))
        {
            var ewsUrl = await QueryAutodiscoverV2Async(autodiscoverUrl, emailAddress, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(ewsUrl))
                return ewsUrl;
        }

        return null;
    }

    private async Task<IReadOnlyList<string>> BuildCandidateAutodiscoverUrlsAsync(string domain, CancellationToken cancellationToken)
    {
        var candidates = new List<string>();

        candidates.AddRange(ResolveScpAutodiscoverUrls(domain));

        var srvUrl = await ResolveSrvAutodiscoverUrlAsync(domain, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(srvUrl))
            candidates.Add(srvUrl);

        candidates.Add($"https://autodiscover.{domain}/autodiscover/autodiscover.json");
        candidates.Add($"https://{domain}/autodiscover/autodiscover.json");

        return candidates;
    }

    private IReadOnlyList<string> ResolveScpAutodiscoverUrls(string emailDomain)
    {
        if (!OperatingSystem.IsWindows())
            return Array.Empty<string>();

        try
        {
            using var connection = new LdapConnection(new LdapDirectoryIdentifier((string)null))
            {
                AuthType = AuthType.Negotiate
            };
            connection.SessionOptions.ProtocolVersion = 3;
            connection.Bind(); // bind as the current logged-in user

            var rootDseResponse = (SearchResponse)connection.SendRequest(
                new SearchRequest(null, "(objectClass=*)", SearchScope.Base, "configurationNamingContext", "defaultNamingContext"));

            if (rootDseResponse.Entries.Count == 0)
                return Array.Empty<string>();

            var rootDse = rootDseResponse.Entries[0];
            var configurationNamingContext = rootDse.Attributes["configurationNamingContext"]?[0]?.ToString();
            var defaultNamingContext = rootDse.Attributes["defaultNamingContext"]?[0]?.ToString();
            if (string.IsNullOrEmpty(configurationNamingContext))
                return Array.Empty<string>();

            // SCP is only valid for mailboxes in this machine's forest.
            if (!IsForestDomain(connection, configurationNamingContext, defaultNamingContext, emailDomain))
                return Array.Empty<string>();

            var scpResponse = (SearchResponse)connection.SendRequest(new SearchRequest(
                configurationNamingContext,
                "(&(objectClass=serviceConnectionPoint)(keywords=77378F46-2C66-4aa9-A6A6-3E7A48B19596))",
                SearchScope.Subtree,
                "serviceBindingInformation"));

            var autodiscoverUrls = new List<string>();
            foreach (SearchResultEntry entry in scpResponse.Entries)
            {
                var binding = entry.Attributes["serviceBindingInformation"];
                if (binding == null)
                    continue;

                foreach (var value in binding.GetValues(typeof(string)))
                {
                    if (Uri.TryCreate(value?.ToString(), UriKind.Absolute, out var uri))
                        autodiscoverUrls.Add(uri.GetLeftPart(UriPartial.Authority) + "/autodiscover/autodiscover.json");
                }
            }

            return autodiscoverUrls;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Autodiscover SCP lookup failed (not domain-joined or AD unreachable).");
            return Array.Empty<string>();
        }
    }

    private static bool IsForestDomain(LdapConnection connection, string configurationNamingContext, string defaultNamingContext, string emailDomain)
    {
        if (string.IsNullOrWhiteSpace(emailDomain))
            return false;

        var forestDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var adDomain = DistinguishedNameToDomain(defaultNamingContext);
        if (!string.IsNullOrEmpty(adDomain))
            forestDomains.Add(adDomain);

        try
        {
            var partitionsResponse = (SearchResponse)connection.SendRequest(new SearchRequest(
                "CN=Partitions," + configurationNamingContext, "(objectClass=*)", SearchScope.Base, "uPNSuffixes"));

            if (partitionsResponse.Entries.Count > 0)
            {
                var suffixes = partitionsResponse.Entries[0].Attributes["uPNSuffixes"];
                if (suffixes != null)
                {
                    foreach (var suffix in suffixes.GetValues(typeof(string)))
                    {
                        var value = suffix?.ToString();
                        if (!string.IsNullOrWhiteSpace(value))
                            forestDomains.Add(value.Trim());
                    }
                }
            }
        }
        catch
        {
        }

        return forestDomains.Contains(emailDomain);
    }

    private static string DistinguishedNameToDomain(string distinguishedName)
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

    private async Task<string> ResolveSrvAutodiscoverUrlAsync(string domain, CancellationToken cancellationToken)
    {
        try
        {
            var lookup = new LookupClient();
            var response = await lookup
                .QueryAsync($"_autodiscover._tcp.{domain}", QueryType.SRV, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            SrvRecord best = null;
            foreach (var record in response.Answers)
            {
                if (record is not SrvRecord srv)
                    continue;

                if (best == null ||
                    srv.Priority < best.Priority ||
                    (srv.Priority == best.Priority && srv.Weight > best.Weight))
                {
                    best = srv;
                }
            }

            if (best == null)
                return null;

            var host = best.Target.Value.TrimEnd('.');
            return $"https://{host}/autodiscover/autodiscover.json";
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Autodiscover SRV lookup failed for {Domain}.", domain);
            return null;
        }
    }

    private async Task<string> QueryAutodiscoverV2Async(string autodiscoverUrl, string emailAddress, CancellationToken cancellationToken)
    {
        var url = $"{autodiscoverUrl}?Email={Uri.EscapeDataString(emailAddress)}&Protocol=EWS";

        try
        {
            using var response = await HttpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("Url", out var urlElement) &&
                urlElement.ValueKind == JsonValueKind.String)
            {
                var ewsUrl = urlElement.GetString();
                if (!string.IsNullOrWhiteSpace(ewsUrl))
                    return ewsUrl;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "EWS Autodiscover V2 probe to {Url} failed.", url);
        }

        return null;
    }
}
