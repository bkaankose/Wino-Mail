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

/// <summary>
/// Discovers the EWS endpoint for an email address following Exchange's Autodiscover precedence:
/// SCP (domain-joined AD) → DNS SRV → <c>autodiscover.{domain}</c> → root domain. Each candidate is
/// queried via the anonymous Autodiscover V2 JSON endpoint (no credentials, so it runs pre-auth).
/// SRV in particular handles split-domain setups where the email domain differs from the Exchange
/// infrastructure domain (e.g. mail addresses on one domain, servers on another).
/// </summary>
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

    /// <summary>
    /// Builds the candidate Autodiscover V2 JSON endpoints in Exchange precedence order.
    /// </summary>
    private async Task<IReadOnlyList<string>> BuildCandidateAutodiscoverUrlsAsync(string domain, CancellationToken cancellationToken)
    {
        var candidates = new List<string>();

        // 1. SCP — Service Connection Point in Active Directory (domain-joined machines).
        candidates.AddRange(ResolveScpAutodiscoverUrls());

        // 2. DNS SRV — _autodiscover._tcp.{domain}; the standard answer for split-domain deployments.
        var srvUrl = await ResolveSrvAutodiscoverUrlAsync(domain, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(srvUrl))
            candidates.Add(srvUrl);

        // 3. autodiscover.{domain}
        candidates.Add($"https://autodiscover.{domain}/autodiscover/autodiscover.json");

        // 4. Root domain.
        candidates.Add($"https://{domain}/autodiscover/autodiscover.json");

        return candidates;
    }

    /// <summary>
    /// SCP (Service Connection Point) discovery via Active Directory. On a domain-joined machine this
    /// finds the Autodiscover SCP(s) published in AD (queried as the logged-in user) and returns their
    /// V2 JSON endpoints. Fails quietly (empty) when not domain-joined or AD is unreachable, so
    /// discovery falls through to SRV and the HTTP candidates. This is the standard answer for
    /// domain-joined clients whose email domain differs from the Exchange infrastructure domain.
    /// </summary>
    private IReadOnlyList<string> ResolveScpAutodiscoverUrls()
    {
        // Serverless LDAP binding relies on the Windows domain DC-locator.
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

            // 1. RootDSE -> configurationNamingContext.
            var rootDseResponse = (SearchResponse)connection.SendRequest(
                new SearchRequest(null, "(objectClass=*)", SearchScope.Base, "configurationNamingContext"));

            if (rootDseResponse.Entries.Count == 0)
                return Array.Empty<string>();

            var configurationNamingContext = rootDseResponse.Entries[0].Attributes["configurationNamingContext"]?[0]?.ToString();
            if (string.IsNullOrEmpty(configurationNamingContext))
                return Array.Empty<string>();

            // 2. Find Autodiscover SCPs (keyword GUID is the well-known Autodiscover marker).
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
                    // serviceBindingInformation is a POX URL (https://host/Autodiscover/Autodiscover.xml);
                    // use its V2 JSON form (anonymous, no credentials needed).
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

    /// <summary>
    /// Resolves <c>_autodiscover._tcp.{domain}</c> SRV to the highest-priority target host and returns
    /// its Autodiscover V2 JSON URL. Returns null when there is no SRV record or DNS is unavailable.
    /// </summary>
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
            // Autodiscover SRV records target an HTTPS host (effectively always port 443).
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
