using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Services;

/// <summary>
/// Detects modern-auth availability and the OAuth authority by replicating Outlook's bootstrap probe:
/// an unauthenticated request carrying the mailbox identity (<c>X-AnchorMailbox</c>) plus
/// <c>X-MS-OpenAuthenticationSupport: True</c> provokes a 401 whose <c>WWW-Authenticate: Bearer</c>
/// challenge carries <c>authorization_uri</c> and <c>issuer_kind</c>. The anchor mailbox is essential —
/// without it the server returns only a generic reactive challenge with no authority.
/// </summary>
public sealed class ExchangeAuthCapabilityProbe : IExchangeAuthCapabilityProbe
{
    private const string AuthorizeSuffix = "/oauth2/authorize";

    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ILogger _logger = Log.ForContext<ExchangeAuthCapabilityProbe>();

    public async Task<ExchangeAuthProbeResult> ProbeAsync(string ewsUrl, string emailAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ewsUrl) || !Uri.TryCreate(ewsUrl, UriKind.Absolute, out var uri))
            return ExchangeAuthProbeResult.Unknown;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            // Empty Bearer + open-auth support + the anchor mailbox: this exact combination makes
            // Exchange return the OAuth authority (authorization_uri) in the Bearer challenge.
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer");
            request.Headers.TryAddWithoutValidation("X-MS-OpenAuthenticationSupport", "True");
            if (!string.IsNullOrWhiteSpace(emailAddress))
            {
                request.Headers.TryAddWithoutValidation("X-AnchorMailbox", emailAddress);
                request.Headers.TryAddWithoutValidation("X-User-Identity", emailAddress);
            }

            using var response = await HttpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.Unauthorized ||
                !response.Headers.TryGetValues("WWW-Authenticate", out var challenges))
            {
                return ExchangeAuthProbeResult.Unknown;
            }

            var challengeList = challenges.ToList();
            var capability = ClassifyChallenges(challengeList);

            var bearerChallenge = challengeList.FirstOrDefault(c =>
                !string.IsNullOrWhiteSpace(c) && c.TrimStart().StartsWith("Bearer", StringComparison.OrdinalIgnoreCase));

            var authorizationUri = bearerChallenge == null ? null : ExtractChallengeParameter(bearerChallenge, "authorization_uri");
            var issuerKind = bearerChallenge == null ? null : ExtractChallengeParameter(bearerChallenge, "issuer_kind");

            return new ExchangeAuthProbeResult
            {
                Capability = capability,
                AuthorizationUri = authorizationUri,
                IssuerKind = issuerKind,
                Authority = DeriveAuthority(authorizationUri)
            };
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Exchange auth capability probe to {EwsUrl} failed.", ewsUrl);
            return ExchangeAuthProbeResult.Unknown;
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

    /// <summary>
    /// Extracts a <c>name="value"</c> parameter from a Bearer challenge string (tolerant of order/spacing).
    /// </summary>
    public static string ExtractChallengeParameter(string bearerChallenge, string parameterName)
    {
        if (string.IsNullOrEmpty(bearerChallenge) || string.IsNullOrEmpty(parameterName))
            return null;

        var marker = parameterName + "=\"";
        var start = bearerChallenge.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        start += marker.Length;
        var end = bearerChallenge.IndexOf('"', start);
        return end < 0 ? null : bearerChallenge[start..end];
    }

    /// <summary>
    /// Derives the OIDC authority from the challenge's authorization_uri by trimming the
    /// <c>/oauth2/authorize</c> suffix (e.g. <c>.../adfs/oauth2/authorize</c> -> <c>.../adfs</c>).
    /// Falls back to the scheme+host when the suffix is absent.
    /// </summary>
    public static string DeriveAuthority(string authorizationUri)
    {
        if (string.IsNullOrWhiteSpace(authorizationUri))
            return null;

        var suffixIndex = authorizationUri.IndexOf(AuthorizeSuffix, StringComparison.OrdinalIgnoreCase);
        if (suffixIndex > 0)
            return authorizationUri[..suffixIndex];

        return Uri.TryCreate(authorizationUri, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : authorizationUri;
    }
}
