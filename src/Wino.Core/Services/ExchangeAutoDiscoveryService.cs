using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Services;

/// <summary>
/// Discovers EWS endpoints with Exchange Autodiscover V2 over the well-known HTTPS hosts. The
/// directory (SCP) and DNS SRV candidates need packages this repository does not carry; the user can
/// always enter the URL by hand when neither host answers.
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

        foreach (var autodiscoverUrl in BuildCandidateAutodiscoverUrls(domain))
        {
            var ewsUrl = await QueryAutodiscoverV2Async(autodiscoverUrl, emailAddress, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(ewsUrl))
                return ewsUrl;
        }

        return null;
    }

    private static IReadOnlyList<string> BuildCandidateAutodiscoverUrls(string domain)
        => (string[])
        [
            $"https://autodiscover.{domain}/autodiscover/autodiscover.json",
            $"https://{domain}/autodiscover/autodiscover.json"
        ];

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "EWS Autodiscover V2 probe to {Url} failed.", url);
        }

        return null;
    }
}
