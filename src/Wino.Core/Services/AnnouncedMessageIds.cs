#nullable enable
using System;
using System.Collections.Generic;

namespace Wino.Core.Services;

/// <summary>
/// The Message-IDs each account announced as new in this session. A message another device moves arrives as a new
/// copy in its new folder; when the old copy was already removed in the same pass, only this memory tells that the
/// message is not new to the account.
/// </summary>
internal sealed class AnnouncedMessageIds
{
    private const int CapacityPerAccount = 2000;

    private readonly object _lock = new();
    private readonly Dictionary<Guid, (HashSet<string> Known, Queue<string> Order)> _accounts = [];

    /// <summary>Records the Message-ID and tells whether it is new for the account.</summary>
    public bool TryAdd(Guid accountId, string messageId)
    {
        lock (_lock)
        {
            if (!_accounts.TryGetValue(accountId, out var entry))
                _accounts[accountId] = entry = (new HashSet<string>(StringComparer.Ordinal), new Queue<string>());

            if (!entry.Known.Add(messageId))
                return false;

            entry.Order.Enqueue(messageId);

            if (entry.Order.Count > CapacityPerAccount)
                entry.Known.Remove(entry.Order.Dequeue());

            return true;
        }
    }
}
