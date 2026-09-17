using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;

namespace Wino.Core.Synchronizers.Exchange;

/// <summary>
/// The EWS and MAPI transports address the same server objects under different ids: EWS by its
/// opaque item id, MAPI by "mapi:" plus the message id. When an account changes transport, the rows
/// stored under the other scheme would be dropped and imported again, losing what only the client
/// knows about them (contact favourites, list memberships and pictures; a task's My Day date and
/// local identity). This matches such rows to the incoming ones by content so they can be re-keyed
/// in place. Translating the ids themselves would need a server round trip per item.
/// </summary>
public static class ExchangeTransportRekey
{
    private const string MapiIdPrefix = "mapi:";

    /// <summary>Whether the two remote ids come from different transports.</summary>
    public static bool IsOtherTransport(string storedRemoteId, string incomingRemoteId)
        => !string.IsNullOrWhiteSpace(storedRemoteId) &&
           !string.IsNullOrWhiteSpace(incomingRemoteId) &&
           IsMapiId(storedRemoteId) != IsMapiId(incomingRemoteId);

    /// <summary>
    /// Stored contacts to re-key, by local id, with the remote id they take. A contact is matched by
    /// its primary e-mail address and display name, and only when that pair is unique on both sides.
    /// </summary>
    public static Dictionary<Guid, string> MatchContacts(IEnumerable<AccountContact> stored, IEnumerable<AccountContact> incoming)
        => Match(
            stored.Where(contact => contact.PendingMutation == ContactPendingMutation.None),
            incoming,
            contact => contact.Id,
            contact => contact.RemoteId,
            ContactKey);

    /// <summary>
    /// Stored tasks to re-key, by local id, with the remote id they take. A task is matched by its
    /// title and due date, and only when that pair is unique on both sides.
    /// </summary>
    public static Dictionary<Guid, string> MatchTasks(IEnumerable<AccountTask> stored, IEnumerable<AccountTask> incoming)
        => Match(
            stored.Where(task => task.PendingMutation == TaskPendingMutation.None),
            incoming,
            task => task.Id,
            task => task.RemoteId,
            TaskKey);

    private static Dictionary<Guid, string> Match<T>(
        IEnumerable<T> stored,
        IEnumerable<T> incoming,
        Func<T, Guid> localId,
        Func<T, string> remoteId,
        Func<T, string> contentKey)
    {
        var incomingRows = incoming.Where(row => !string.IsNullOrWhiteSpace(remoteId(row))).ToList();
        var incomingIds = incomingRows.Select(remoteId).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<Guid, string>();

        if (incomingRows.Count == 0)
            return result;

        // Only rows the other transport stored are candidates; a row this transport already knows
        // keeps its id, and an incoming row that is already stored is not offered twice.
        var sample = remoteId(incomingRows[0]);
        var storedRows = stored
            .Where(row => IsOtherTransport(remoteId(row), sample) && !incomingIds.Contains(remoteId(row)))
            .ToList();

        if (storedRows.Count == 0)
            return result;

        var storedIds = stored.Select(remoteId).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        var incomingByKey = incomingRows
            .Where(row => !storedIds.Contains(remoteId(row)))
            .Select(row => (Row: row, Key: contentKey(row)))
            .Where(entry => entry.Key is not null)
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().Row, StringComparer.Ordinal);

        foreach (var group in storedRows
                     .Select(row => (Row: row, Key: contentKey(row)))
                     .Where(entry => entry.Key is not null)
                     .GroupBy(entry => entry.Key, StringComparer.Ordinal)
                     .Where(group => group.Count() == 1))
        {
            if (incomingByKey.TryGetValue(group.Key, out var match))
                result[localId(group.First().Row)] = remoteId(match);
        }

        return result;
    }

    private static bool IsMapiId(string remoteId)
        => remoteId.StartsWith(MapiIdPrefix, StringComparison.Ordinal);

    private static string ContactKey(AccountContact contact)
    {
        var address = contact.PrimaryEmailAddress?.Trim().ToLowerInvariant() ?? string.Empty;
        var name = contact.DisplayName?.Trim().ToLowerInvariant() ?? string.Empty;

        return address.Length == 0 && name.Length == 0 ? null : address + "\n" + name;
    }

    private static string TaskKey(AccountTask task)
    {
        var title = task.Title?.Trim().ToLowerInvariant() ?? string.Empty;

        return title.Length == 0
            ? null
            : title + "\n" + (task.DueDate?.ToString("yyyyMMdd", CultureInfo.InvariantCulture) ?? string.Empty);
    }
}
