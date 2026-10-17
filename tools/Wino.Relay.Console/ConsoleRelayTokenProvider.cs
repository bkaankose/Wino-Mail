using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Relay.ConsoleApp;

/// <summary>
/// Relay tokens from <c>POST /api/v1/relay/token</c> with the app's access token as it is. Unlike the app's provider it
/// never refreshes the Wino tokens; a rejected access token surfaces as <c>sign_in_required</c> with a hint.
/// </summary>
internal sealed class ConsoleRelayTokenProvider(HttpClient http, Func<string> accessToken) : IPushRelayTokenProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PushRelayAccessToken? _cached;

    public int? MailboxLimit => _cached?.MailboxLimit;

    public async Task<PushRelayAccessToken> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is { } cached && DateTimeOffset.UtcNow < cached.ExpiresUtc - PushRelayDefaults.TokenRenewalMargin)
                return cached;

            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/relay/token");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken());
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                throw new PushRelayApiException(new PushRelayProblem(PushRelayErrorCodes.ServiceUnavailable, 0, Detail: $"Wino API unreachable: {ex.Message}"), ex);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new PushRelayApiException(new PushRelayProblem(PushRelayErrorCodes.SignInRequired, 401,
                        Detail: "The app's access token was rejected. Open Wino Mail so it refreshes the token, then press r."));

                var envelope = response.IsSuccessStatusCode ? JsonSerializer.Deserialize(body, ConsoleJsonContext.Default.RelayTokenEnvelope) : null;
                if (envelope?.Result is not { Token.Length: > 0 } result)
                    throw new PushRelayApiException(new PushRelayProblem(PushRelayErrorCodes.ServiceUnavailable, (int)response.StatusCode,
                        Detail: envelope?.ErrorCode ?? envelope?.Message ?? $"HTTP {(int)response.StatusCode}"));

                _cached = new PushRelayAccessToken(result.Token, result.ExpiresUtc, ResolveBaseUri(result.RelayUrl), null);
                return _cached;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => _cached = null;

    /// <summary>The relay base address from the token response, or the default; mirrors the app's provider.</summary>
    private static Uri ResolveBaseUri(string? relayUrl)
    {
        if (!string.IsNullOrWhiteSpace(relayUrl) && Uri.TryCreate(relayUrl.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            var text = uri.GetLeftPart(UriPartial.Path);
            return new Uri(text.EndsWith('/') ? text : text + "/");
        }

        return new Uri(PushRelayDefaults.DefaultBaseUrl);
    }
}

internal sealed record RelayTokenEnvelope(RelayTokenDto? Result, string? ErrorCode, string? Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RelayTokenEnvelope))]
[JsonSerializable(typeof(PushRelayDeviceIdentity))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class ConsoleJsonContext : JsonSerializerContext;
