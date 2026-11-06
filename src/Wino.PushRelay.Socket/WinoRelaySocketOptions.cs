#nullable enable
using System;

namespace Wino.PushRelay.Socket;

/// <summary>Timing of the socket client. The defaults suit the relay's 15 s keepalive and its connect-rate limits.</summary>
public sealed class WinoRelaySocketOptions
{
    /// <summary>Budget for one connect attempt (negotiate, WebSocket upgrade and handshake).</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long <c>StartAsync</c> waits for the relay's <c>hello</c> before returning without it.</summary>
    public TimeSpan HelloTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether a dropped socket is reopened automatically (never after <c>bye revoked|abuse</c>; after <c>bye replaced</c>
    /// at most <see cref="MaxReplacedReconnects"/> times per <see cref="ReplacedReconnectWindow"/>).
    /// </summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>
    /// More <c>bye replaced</c> reconnects than <see cref="MaxReplacedReconnects"/> within this window mean another install
    /// uses this device identity; the client then stays down instead of displacing it in a loop.
    /// </summary>
    public TimeSpan ReplacedReconnectWindow { get; set; } = TimeSpan.FromHours(1);

    public int MaxReplacedReconnects { get; set; } = 3;

    /// <summary>
    /// The quick reconnect attempts. After the last one each delay doubles up to <see cref="ReconnectCeiling"/>. Every delay
    /// gets ±50 % jitter.
    /// </summary>
    public TimeSpan[] ReconnectDelays { get; set; } =
    [
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2)
    ];

    public TimeSpan ReconnectCeiling { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>After <c>bye restart</c> the first reconnect waits a uniform random delay up to this, so a relay restart is not a reconnect storm.</summary>
    public TimeSpan RestartSpread { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Longest wait honoured from a relay <c>Retry-After</c>.</summary>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// After <c>hello</c> reports unacknowledged events, fetch them over HTTP and raise them as replayed events, so a
    /// desktop that was offline catches up. Acknowledging them stays with the consumer, as for live events.
    /// </summary>
    public bool ReplayUnackedOnConnect { get; set; } = true;

    /// <summary>Events per HTTP page during a replay (the relay clamps it).</summary>
    public int ReplayPageSize { get; set; } = 100;

    /// <summary>Shortest interval between two well-known refreshes caused by an unknown relay key id.</summary>
    public TimeSpan WellKnownRefreshInterval { get; set; } = TimeSpan.FromMinutes(1);
}
