using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Services;

/// <summary>
/// Detects modern-auth availability by presenting a deliberately invalid bearer token to the EWS
/// endpoint. An OAuth-enabled Exchange answers with a <c>WWW-Authenticate: Bearer ...</c> challenge
/// (even when the bare anonymous challenge lists only legacy schemes); a Basic/NTLM-only endpoint
/// does not. Endpoint/server-level, not per-mailbox — used as a smart default, never a guarantee.
/// </summary>
public sealed class ExchangeAuthCapabilityProbe : IExchangeAuthCapabilityProbe
{
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ILogger _logger = Log.ForContext<ExchangeAuthCapabilityProbe>();

    public async Task<ExchangeAuthCapability> ProbeAsync(string ewsUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ewsUrl) || !Uri.TryCreate(ewsUrl, UriKind.Absolute, out var uri))
            return ExchangeAuthCapability.Unknown;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer invalidtoken");

            using var response = await HttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.Unauthorized)
                return ExchangeAuthCapability.Unknown;

            return response.Headers.TryGetValues("WWW-Authenticate", out var challenges)
                ? ClassifyChallenges(challenges)
                : ExchangeAuthCapability.Unknown;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Exchange auth capability probe to {EwsUrl} failed.", ewsUrl);
            return ExchangeAuthCapability.Unknown;
        }
    }

    /// <summary>
    /// Classifies the WWW-Authenticate challenge values: a Bearer scheme means modern auth is
    /// available; any challenge without Bearer means Basic/NTLM only; no challenge is inconclusive.
    /// </summary>
    public static ExchangeAuthCapability ClassifyChallenges(IEnumerable<string> wwwAuthenticateValues)
    {
        if (wwwAuthenticateValues == null)
            return ExchangeAuthCapability.Unknown;

        var sawAny = false;
        foreach (var challenge in wwwAuthenticateValues)
        {
            sawAny = true;
            if (!string.IsNullOrWhiteSpace(challenge) &&
                challenge.TrimStart().StartsWith("Bearer", StringComparison.OrdinalIgnoreCase))
            {
                return ExchangeAuthCapability.ModernAuthAvailable;
            }
        }

        return sawAny ? ExchangeAuthCapability.BasicOnly : ExchangeAuthCapability.Unknown;
    }
}
