#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.PushRelay;

namespace Wino.Core.PushRelay;

/// <summary>The relay's <c>/.well-known/winorelay</c> document (Pub/Sub topic, Graph URLs), refreshed once an hour.</summary>
public sealed class RelayWellKnownCache(IPushRelayClient client, TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private PushRelayWellKnown? _cached;
    private DateTimeOffset _fetchedAt;

    public async Task<PushRelayWellKnown> GetAsync(CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        if (_cached is { } cached && now - _fetchedAt < Lifetime)
            return cached;

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (_cached is { } again && now - _fetchedAt < Lifetime)
                return again;

            var fresh = await client.GetWellKnownAsync(cancellationToken).ConfigureAwait(false);
            _cached = fresh;
            _fetchedAt = now;
            return fresh;
        }
        finally
        {
            _lock.Release();
        }
    }
}
