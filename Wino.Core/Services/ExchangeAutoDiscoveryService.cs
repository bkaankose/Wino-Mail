using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Services;

/// <summary>
/// Discovers the EWS endpoint via the anonymous Autodiscover V2 JSON endpoint
/// (<c>/autodiscover/autodiscover.json?Email=...&amp;Protocol=EWS</c>). V2 needs no
/// credentials, so it can run before the user has authenticated.
/// </summary>
public sealed class ExchangeAutoDiscoveryService : IExchangeAutoDiscoveryService
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ILogger _logger = Log.ForContext<ExchangeAutoDiscoveryService>();

    public async Task<string> TryDiscoverEwsUrlAsync(string emailAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(emailAddress) || !emailAddress.Contains('@'))
            return null;

        var domain = emailAddress.Split('@')[^1].Trim();
        if (string.IsNullOrWhiteSpace(domain))
            return null;

        foreach (var host in new[] { $"autodiscover.{domain}", domain })
        {
            var url = $"https://{host}/autodiscover/autodiscover.json?Email={Uri.EscapeDataString(emailAddress)}&Protocol=EWS";

            try
            {
                using var response = await HttpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;

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
        }

        return null;
    }
}
