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

/// <summary>Detects Exchange modern-auth support from the EWS authentication challenge.</summary>
public sealed class ExchangeAuthCapabilityProbe : IExchangeAuthCapabilityProbe
{
    private const string AuthorizeSuffix = "/oauth2/authorize";

    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ILogger _logger = Log.ForContext<ExchangeAuthCapabilityProbe>();

    public async Task<ExchangeAuthProbeResult> ProbeAsync(string ewsUrl, string emailAddress, CancellationToken cancellationToken = default)
    {
        // The probe carries the mailbox identity, so require HTTPS before sending it.
        if (string.IsNullOrWhiteSpace(ewsUrl) ||
            !Uri.TryCreate(ewsUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return ExchangeAuthProbeResult.Unknown;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            // These headers make Exchange include the OAuth authority in the Bearer challenge.
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

    public static ExchangeAuthCapability ClassifyChallenges(IEnumerable<string> wwwAuthenticateValues)
    {
        if (wwwAuthenticateValues == null)
            return ExchangeAuthCapability.Unknown;

        var sawAny = false;
        foreach (var challenge in wwwAuthenticateValues)
        {
            sawAny = true;

            // A bare Bearer challenge is not enough; modern auth needs an authority.
            if (!string.IsNullOrWhiteSpace(challenge) &&
                challenge.TrimStart().StartsWith("Bearer", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(ExtractChallengeParameter(challenge, "authorization_uri")))
            {
                return ExchangeAuthCapability.ModernAuthAvailable;
            }
        }

        return sawAny ? ExchangeAuthCapability.BasicOnly : ExchangeAuthCapability.Unknown;
    }

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
