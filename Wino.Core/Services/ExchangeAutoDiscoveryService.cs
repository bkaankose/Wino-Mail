using System;
using System.Collections.Generic;
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
    /// SCP (AD) discovery. Implemented in a follow-up (needs an LDAP dependency); yields nothing for now,
    /// so discovery falls through to SRV and the HTTP candidates.
    /// </summary>
    private IReadOnlyList<string> ResolveScpAutodiscoverUrls() => Array.Empty<string>();

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
