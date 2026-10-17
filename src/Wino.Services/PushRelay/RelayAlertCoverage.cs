#nullable enable
using System;
using System.Collections.Concurrent;
using Wino.Core.Domain.Interfaces;

namespace Wino.Services.PushRelay;

public sealed class RelayAlertCoverage(TimeProvider? timeProvider = null) : IRelayAlertCoverage
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _until = new();

    public void BeginCoverage(Guid accountId, TimeSpan duration)
        => _until[accountId] = _timeProvider.GetUtcNow() + duration;

    public bool IsCovered(Guid accountId)
    {
        if (!_until.TryGetValue(accountId, out var until)) return false;
        if (_timeProvider.GetUtcNow() <= until) return true;
        _until.TryRemove(accountId, out _);
        return false;
    }
}
