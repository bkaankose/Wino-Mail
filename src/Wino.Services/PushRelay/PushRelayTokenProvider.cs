#nullable enable
using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Wino.Core.Domain;
using Wino.Core.Domain.Exceptions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Services.PushRelay;

/// <summary>
/// Gets relay tokens from the Wino Mail API and caches them until <see cref="PushRelayDefaults.TokenRenewalMargin"/>
/// before they expire. Concurrent callers share one API call. The token is never logged.
/// </summary>
public sealed class PushRelayTokenProvider : IPushRelayTokenProvider
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    private readonly IWinoAccountApiClient _apiClient;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger = Log.ForContext<PushRelayTokenProvider>();
    private readonly object _gate = new();
    private PushRelayAccessToken? _cached;
    private Task<PushRelayAccessToken>? _pending;
    private int _generation;
    private int? _mailboxLimit;

    public PushRelayTokenProvider(IWinoAccountApiClient apiClient, TimeProvider? timeProvider = null)
    {
        _apiClient = apiClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int? MailboxLimit { get { lock (_gate) return _mailboxLimit; } }

    public Task<PushRelayAccessToken> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        Task<PushRelayAccessToken> pending;
        lock (_gate)
        {
            if (_cached is { } cached && _timeProvider.GetUtcNow() < cached.ExpiresUtc - PushRelayDefaults.TokenRenewalMargin)
                return Task.FromResult(cached);

            // One shared call for every waiter; a single caller's cancellation must not fail the others.
            pending = _pending ??= FetchAsync(_generation);
        }

        return pending.WaitAsync(cancellationToken);
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
            _pending = null;
            _generation++;
        }
    }

    private async Task<PushRelayAccessToken> FetchAsync(int generation)
    {
        await Task.Yield();
        try
        {
            using var timeout = new CancellationTokenSource(FetchTimeout);
            var envelope = await _apiClient.CreateRelayTokenAsync(timeout.Token).ConfigureAwait(false);

            if (!envelope.IsSuccess || envelope.Result is not { } result || string.IsNullOrWhiteSpace(result.Token))
                throw MapFailure(envelope.IsSuccess ? WinoAccountClientErrorCodes.InvalidServiceResponse : envelope.ErrorCode);

            var token = new PushRelayAccessToken(result.Token, result.ExpiresUtc, ResolveBaseUri(result.RelayUrl), ReadMailboxLimit(result.Token));
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _cached = token;
                    if (token.MailboxLimit is { } limit)
                        _mailboxLimit = limit;
                }
            }

            return token;
        }
        catch (OperationCanceledException ex)
        {
            throw new PushRelayApiException(new PushRelayProblem(PushRelayErrorCodes.ServiceUnavailable, 0, Detail: "Relay token request timed out."), ex);
        }
        finally
        {
            lock (_gate)
            {
                if (generation == _generation)
                    _pending = null;
            }
        }
    }

    private PushRelayApiException MapFailure(string? errorCode)
    {
        if (errorCode is WinoAccountClientErrorCodes.SignInRequired or WinoAccountClientErrorCodes.AccountSessionChanged
            || errorCode?.StartsWith("HTTP 401", StringComparison.Ordinal) == true)
        {
            return new PushRelayApiException(new PushRelayProblem(PushRelayErrorCodes.SignInRequired, 0, Detail: errorCode));
        }

        // RELAY_UNAVAILABLE (no signing key), an unreachable API and anything unexpected are retried with backoff.
        _logger.Warning("Relay token request failed with error code {ErrorCode}.", errorCode);
        return new PushRelayApiException(new PushRelayProblem(PushRelayErrorCodes.ServiceUnavailable, 0, Detail: errorCode));
    }

    internal static Uri ResolveBaseUri(string? relayUrl)
    {
        if (!string.IsNullOrWhiteSpace(relayUrl)
            && Uri.TryCreate(relayUrl.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            var text = uri.GetLeftPart(UriPartial.Path);
            return new Uri(text.EndsWith('/') ? text : text + "/");
        }

        return new Uri(PushRelayDefaults.DefaultBaseUrl);
    }

    /// <summary>Reads the <c>wr_mbx</c> claim for display only; the relay enforces the limit itself.</summary>
    internal static int? ReadMailboxLimit(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2)
                return null;

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            if (!document.RootElement.TryGetProperty("wr_mbx", out var claim))
                return null;

            if (claim.ValueKind == JsonValueKind.Number && claim.TryGetInt32(out var number) && number > 0)
                return number;

            return claim.ValueKind == JsonValueKind.String && int.TryParse(claim.GetString(), out var parsed) && parsed > 0 ? parsed : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}
