#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Services.PushRelay;

/// <summary>
/// HTTP client for WinoRelay <c>/v1</c>. The base address comes from the relay token's <c>relayUrl</c>.
/// Request bodies, tokens and credentials are never logged.
/// </summary>
public sealed class PushRelayClient : IPushRelayClient, IDisposable
{
    public const string DeviceIdHeaderName = "X-Device-Id";
    private const string ProblemTypePrefix = "urn:winorelay:error:";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly IPushRelayTokenProvider _tokens;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly ILogger _logger = Log.ForContext<PushRelayClient>();

    public PushRelayClient(IPushRelayTokenProvider tokens, HttpClient? httpClient = null)
    {
        _tokens = tokens;
        if (httpClient != null)
        {
            _httpClient = httpClient;
            return;
        }

        _httpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.Brotli | DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
        {
            Timeout = RequestTimeout
        };
        _ownsHttpClient = true;
    }

    public Task<PushRelayWellKnown> GetWellKnownAsync(CancellationToken cancellationToken = default)
        => SendRequiredAsync(HttpMethod.Get, ".well-known/winorelay", null, null, PushRelayJsonContext.Default.PushRelayWellKnown, cancellationToken);

    public Task<PushRelayRegisterDeviceResponse> RegisterDeviceAsync(PushRelayRegisterDeviceRequest request, CancellationToken cancellationToken = default)
        => SendRequiredAsync(HttpMethod.Post, "v1/devices", null,
            () => JsonContent.Create(request, PushRelayJsonContext.Default.PushRelayRegisterDeviceRequest),
            PushRelayJsonContext.Default.PushRelayRegisterDeviceResponse, cancellationToken);

    public Task<PushRelayDevice> GetDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
        => SendRequiredAsync(HttpMethod.Get, "v1/devices/me", deviceId, null, PushRelayJsonContext.Default.PushRelayDevice, cancellationToken);

    public Task<PushRelayDevice> UpdateDeviceAsync(Guid deviceId, PushRelayUpdateDeviceRequest request, CancellationToken cancellationToken = default)
        => SendRequiredAsync(HttpMethod.Patch, "v1/devices/me", deviceId,
            () => JsonContent.Create(request, PushRelayJsonContext.Default.PushRelayUpdateDeviceRequest),
            PushRelayJsonContext.Default.PushRelayDevice, cancellationToken);

    public Task DeleteDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
        => SendAsync<object>(HttpMethod.Delete, "v1/devices/me", deviceId, null, null, cancellationToken);

    public Task<PushRelayPushTestResponse> SendPushTestAsync(Guid deviceId, CancellationToken cancellationToken = default)
        => SendRequiredAsync(HttpMethod.Post, "v1/devices/me/push-test", deviceId, null, PushRelayJsonContext.Default.PushRelayPushTestResponse, cancellationToken);

    public Task<PushRelayDeviceEventList> ListDeviceEventsAsync(Guid deviceId, Guid? since = null, int? limit = null, bool unackedOnly = false, CancellationToken cancellationToken = default)
    {
        var query = new List<string>(3);
        if (since is { } sinceId) query.Add("since=" + sinceId.ToString("D"));
        if (limit is { } pageSize) query.Add("limit=" + pageSize.ToString(CultureInfo.InvariantCulture));
        if (unackedOnly) query.Add("unacked=true");
        var route = query.Count == 0 ? "v1/devices/me/events" : "v1/devices/me/events?" + string.Join('&', query);
        return SendRequiredAsync(HttpMethod.Get, route, deviceId, null, PushRelayJsonContext.Default.PushRelayDeviceEventList, cancellationToken);
    }

    public Task<PushRelayAckEventsResponse> AckDeviceEventsAsync(Guid deviceId, PushRelayAckEventsRequest request, CancellationToken cancellationToken = default)
        => SendRequiredAsync(HttpMethod.Post, "v1/devices/me/events/ack", deviceId,
            () => JsonContent.Create(request, PushRelayJsonContext.Default.PushRelayAckEventsRequest),
            PushRelayJsonContext.Default.PushRelayAckEventsResponse, cancellationToken);

    public Task<PushRelaySyncAccountsResponse> SyncAccountsAsync(Guid deviceId, List<string> clientAccountIds, CancellationToken cancellationToken = default)
        => SendRequiredAsync(HttpMethod.Put, "v1/devices/me/accounts", deviceId,
            () => JsonContent.Create(new PushRelaySyncAccountsRequest(clientAccountIds), PushRelayJsonContext.Default.PushRelaySyncAccountsRequest),
            PushRelayJsonContext.Default.PushRelaySyncAccountsResponse, cancellationToken);

    public Task<PushRelayMailbox> CreateMailboxAsync(Guid deviceId, PushRelayCreateMailboxRequest request, CancellationToken cancellationToken = default)
        => SendRequiredAsync(HttpMethod.Post, "v1/mailboxes", deviceId,
            () => JsonContent.Create(request, PushRelayJsonContext.Default.PushRelayCreateMailboxRequest),
            PushRelayJsonContext.Default.PushRelayMailbox, cancellationToken);

    public Task<PushRelayMailbox> UpdateMailboxCredentialsAsync(Guid deviceId, Guid subscriptionId, PushRelayUpdateCredentialsRequest request, CancellationToken cancellationToken = default)
        => SendRequiredAsync(HttpMethod.Put, $"v1/mailboxes/{subscriptionId:D}/credentials", deviceId,
            () => JsonContent.Create(request, PushRelayJsonContext.Default.PushRelayUpdateCredentialsRequest),
            PushRelayJsonContext.Default.PushRelayMailbox, cancellationToken);

    public Task DeleteMailboxAsync(Guid deviceId, Guid subscriptionId, CancellationToken cancellationToken = default)
        => SendAsync<object>(HttpMethod.Delete, $"v1/mailboxes/{subscriptionId:D}", deviceId, null, null, cancellationToken);

    public Task<PushRelayResyncResponse> ResyncMailboxAsync(Guid deviceId, Guid subscriptionId, CancellationToken cancellationToken = default)
        => SendRequiredAsync(HttpMethod.Post, $"v1/mailboxes/{subscriptionId:D}/resync", deviceId, null,
            PushRelayJsonContext.Default.PushRelayResyncResponse, cancellationToken);

    public Task<PushRelayMailboxList> ListMailboxesAsync(Guid deviceId, CancellationToken cancellationToken = default)
        => SendRequiredAsync(HttpMethod.Get, "v1/mailboxes", deviceId, null, PushRelayJsonContext.Default.PushRelayMailboxList, cancellationToken);

    private async Task<TResponse> SendRequiredAsync<TResponse>(HttpMethod method, string route, Guid? deviceId,
        Func<HttpContent>? contentFactory, JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken)
        => await SendAsync(method, route, deviceId, contentFactory, responseType, cancellationToken).ConfigureAwait(false)
           ?? throw InvalidResponse(0);

    private async Task<TResponse?> SendAsync<TResponse>(HttpMethod method, string route, Guid? deviceId,
        Func<HttpContent>? contentFactory, JsonTypeInfo<TResponse>? responseType, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var token = await _tokens.GetTokenAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(method, new Uri(token.RelayBaseUri, route));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (deviceId is { } id)
                request.Headers.TryAddWithoutValidation(DeviceIdHeaderName, id.ToString("D"));
            request.Content = contentFactory?.Invoke();

            using var response = await SendCoreAsync(request, route, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return responseType is null ? default : await ReadBodyAsync(response, responseType, cancellationToken).ConfigureAwait(false);

            var problem = await ReadProblemAsync(response, cancellationToken).ConfigureAwait(false);
            var exception = new PushRelayApiException(problem);

            // An expired or rejected relay token is renewed once. device_revoked goes to the caller.
            if (exception.IsTokenRejected && attempt == 0)
            {
                _tokens.Invalidate();
                continue;
            }

            _logger.Information("WinoRelay {Method} {Route} failed with HTTP {Status} ({Code}/{Reason}).",
                method.Method, route, problem.Status, problem.Code, problem.Reason);
            throw exception;
        }
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, string route, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.Information("WinoRelay {Route} is unreachable: {Reason}", route, ex.GetBaseException().GetType().Name);
            throw new PushRelayApiException(new PushRelayProblem(PushRelayErrorCodes.ServiceUnavailable, 0), ex);
        }
    }

    private static async Task<TResponse> ReadBodyAsync<TResponse>(HttpResponseMessage response, JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        string payload;
        try
        {
            payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new PushRelayApiException(new PushRelayProblem(PushRelayErrorCodes.ServiceUnavailable, status), ex);
        }

        if (string.IsNullOrWhiteSpace(payload) || IsHtml(response))
            throw InvalidResponse(status);

        try
        {
            return JsonSerializer.Deserialize(payload, responseType) ?? throw InvalidResponse(status);
        }
        catch (JsonException ex)
        {
            throw new PushRelayApiException(new PushRelayProblem(PushRelayErrorCodes.InvalidResponse, status), ex);
        }
    }

    private static async Task<PushRelayProblem> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        var retryAfter = ReadRetryAfter(response);

        string? payload = null;
        try
        {
            payload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // Treated like a body that is not a problem document.
        }

        if (!string.IsNullOrWhiteSpace(payload) && !IsHtml(response))
        {
            try
            {
                var fields = JsonSerializer.Deserialize(payload, PushRelayJsonContext.Default.DictionaryStringJsonElement);
                if (fields != null && ReadString(fields, "type") is { } type)
                {
                    var code = type.StartsWith(ProblemTypePrefix, StringComparison.Ordinal) ? type[ProblemTypePrefix.Length..] : type;
                    return new PushRelayProblem(
                        code,
                        status,
                        ReadString(fields, "title"),
                        ReadString(fields, "detail"),
                        ReadString(fields, "reason"),
                        ReadInt(fields, "retryAfterSeconds") ?? retryAfter,
                        Guid.TryParse(ReadString(fields, "subscriptionId"), out var subscriptionId) ? subscriptionId : null);
                }
            }
            catch (JsonException)
            {
                // Falls through to the status-based mapping below.
            }
        }

        var fallback = status switch
        {
            429 => PushRelayErrorCodes.RateLimited,
            >= 500 or 408 => PushRelayErrorCodes.ServiceUnavailable,
            _ => PushRelayErrorCodes.InvalidResponse
        };
        return new PushRelayProblem(fallback, status, RetryAfterSeconds: retryAfter);
    }

    private static int? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } retryAfter)
            return null;

        if (retryAfter.Delta is { } delta)
            return (int)Math.Ceiling(delta.TotalSeconds);

        return retryAfter.Date is { } date ? Math.Max(0, (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds)) : null;
    }

    private static string? ReadString(Dictionary<string, JsonElement> fields, string name)
        => fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt(Dictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;

        return value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static bool IsHtml(HttpResponseMessage response)
        => response.Content.Headers.ContentType?.MediaType is string mediaType
           && (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
               || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase));

    private static PushRelayApiException InvalidResponse(int status)
        => new(new PushRelayProblem(status >= 500 ? PushRelayErrorCodes.ServiceUnavailable : PushRelayErrorCodes.InvalidResponse, status));

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
