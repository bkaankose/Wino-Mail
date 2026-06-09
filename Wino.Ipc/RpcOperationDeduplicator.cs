using System;
using System.Collections.Generic;

namespace Wino.Ipc;

/// <summary>
/// LRU cache of recently completed write operations keyed by the client generated operation id.
/// Write calls are retried once by the client after a reconnect (at-least-once delivery);
/// this window turns that into effectively-once for the duration of the cache.
/// </summary>
public sealed class RpcOperationDeduplicator
{
    private readonly object _lock = new();
    private readonly int _capacity;
    private readonly Dictionary<Guid, LinkedListNode<CacheEntry>> _entries;
    private readonly LinkedList<CacheEntry> _lruOrder = new();

    public RpcOperationDeduplicator(int capacity = 512)
    {
        _capacity = capacity;
        _entries = new Dictionary<Guid, LinkedListNode<CacheEntry>>(capacity);
    }

    public bool TryGetCompletedResponse(Guid operationId, out byte[]? responsePayload)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(operationId, out var node))
            {
                _lruOrder.Remove(node);
                _lruOrder.AddFirst(node);
                responsePayload = node.Value.ResponsePayload;
                return true;
            }
        }

        responsePayload = null;
        return false;
    }

    public void RecordCompletedOperation(Guid operationId, byte[]? responsePayload)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(operationId, out var existing))
            {
                _lruOrder.Remove(existing);
                _lruOrder.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(operationId, responsePayload));
            _lruOrder.AddFirst(node);
            _entries[operationId] = node;

            while (_entries.Count > _capacity)
            {
                var oldest = _lruOrder.Last!;
                _lruOrder.RemoveLast();
                _entries.Remove(oldest.Value.OperationId);
            }
        }
    }

    private readonly record struct CacheEntry(Guid OperationId, byte[]? ResponsePayload);
}
