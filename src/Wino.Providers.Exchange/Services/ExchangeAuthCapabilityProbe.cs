#nullable enable
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
using Wino.Core.Domain.Models.Accounts;

namespace Wino.Core.Services;

/// <summary>Detects Exchange modern-auth support from the EWS authentication challenge.</summary>
public sealed class ExchangeAuthCapabilityProbe : IExchangeAuthCapabilityProbe
{
    private const string AuthorizeSuffix = "/oauth2/authorize";

    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly ILogger _logger = Log.ForContext<ExchangeAuthCapabilityProbe>();

    public async Task<ExchangeAuthProbeResult> ProbeAsync(string ewsUrl, string? emailAddress, CancellationToken cancellationToken = default)
    {
        // The probe carries the mailbox identity, so it is only sent over HTTPS.
        if (string.IsNullOrWhiteSpace(ewsUrl) ||
            !Uri.TryCreate(ewsUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return ExchangeAuthProbeResult.Unknown;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            // An empty Bearer header plus these hints make Exchange name the OAuth authority in its Bearer challenge.
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
            var bearerChallenge = challengeList.FirstOrDefault(IsBearerChallenge);
            var authorizationUri = ExtractChallengeParameter(bearerChallenge, "authorization_uri");

            return new ExchangeAuthProbeResult
            {
                Capability = Classify(challengeList),
                AuthorizationUri = authorizationUri,
                IssuerKind = ExtractChallengeParameter(bearerChallenge, "issuer_kind"),
                Authority = DeriveAuthority(authorizationUri)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Exchange authentication probe to {EwsUrl} failed.", ewsUrl);
            return ExchangeAuthProbeResult.Unknown;
        }
    }

    private static ExchangeAuthCapability Classify(List<string> challenges)
    {
        if (challenges.Count == 0)
            return ExchangeAuthCapability.Unknown;

        // A bare Bearer challenge is not enough: modern auth needs an authorization server to sign in with.
        return challenges.Any(challenge => IsBearerChallenge(challenge) && ExtractChallengeParameter(challenge, "authorization_uri") is not null)
            ? ExchangeAuthCapability.ModernAuthAvailable
            : ExchangeAuthCapability.BasicOnly;
    }

    private static bool IsBearerChallenge(string? challenge)
        => !string.IsNullOrWhiteSpace(challenge) && challenge.TrimStart().StartsWith("Bearer", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractChallengeParameter(string? challenge, string parameterName)
    {
        if (string.IsNullOrEmpty(challenge))
            return null;

        var marker = parameterName + "=\"";
        var start = challenge.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        start += marker.Length;
        var end = challenge.IndexOf('"', start);
        return end < 0 || end == start ? null : challenge[start..end];
    }

    private static string? DeriveAuthority(string? authorizationUri)
    {
        if (string.IsNullOrWhiteSpace(authorizationUri))
            return null;

        var suffixIndex = authorizationUri.IndexOf(AuthorizeSuffix, StringComparison.OrdinalIgnoreCase);
        if (suffixIndex > 0)
            return authorizationUri[..suffixIndex];

        return Uri.TryCreate(authorizationUri, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : null;
    }
}
