#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Wino.Core.Domain.Models.Intelligence;

/// <summary>
/// Everything the coverage editor needs to know about an account's mail, loaded once.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately not a list of <see cref="IntelligenceMessageCandidate"/>. That record
/// carries subject, sender, thread and body-locator data the editor never reads, and two arrays
/// per row; a large mailbox costs tens of megabytes to hold and several times that to build.
/// Here a message is an <em>index</em>: its identity and date live in two parallel arrays, and a
/// folder is just the list of indices it contains.
/// </para>
/// <para>
/// Messages are distinct by <c>RemoteMessageId</c> and ordered by received date descending, then
/// by id ordinal ascending. That order is load-bearing twice over: it makes
/// <see cref="ReceivedAtUtcTicks"/> non-increasing along any folder's index list, so a date range
/// is two binary searches and a latest-N selection is deterministic.
/// </para>
/// </remarks>
public sealed class IntelligenceCoverageInventory
{
    private readonly Dictionary<string, int> _indicesByRemoteMessageId;

    public IntelligenceCoverageInventory(
        Guid localAccountId,
        string[] remoteMessageIds,
        long[] receivedAtUtcTicks,
        IReadOnlyDictionary<string, int[]> folderMessageIndices,
        DateTimeOffset builtAtUtc)
    {
        if (remoteMessageIds.Length != receivedAtUtcTicks.Length)
            throw new ArgumentException("The identity and date columns must be the same length.", nameof(receivedAtUtcTicks));

        LocalAccountId = localAccountId;
        RemoteMessageIds = remoteMessageIds;
        ReceivedAtUtcTicks = receivedAtUtcTicks;
        FolderMessageIndices = folderMessageIndices;
        BuiltAtUtc = builtAtUtc;

        _indicesByRemoteMessageId = new Dictionary<string, int>(remoteMessageIds.Length, StringComparer.Ordinal);
        for (var index = 0; index < remoteMessageIds.Length; index++)
            _indicesByRemoteMessageId[remoteMessageIds[index]] = index;
    }

    public Guid LocalAccountId { get; }

    public DateTimeOffset BuiltAtUtc { get; }

    /// <summary>Distinct messages, newest first. The position in this array is the message handle.</summary>
    public string[] RemoteMessageIds { get; }

    /// <summary>Parallel to <see cref="RemoteMessageIds"/>. UTC ticks, non-increasing.</summary>
    public long[] ReceivedAtUtcTicks { get; }

    /// <summary>Per remote folder id, the indices it contains, in the same newest-first order.</summary>
    public IReadOnlyDictionary<string, int[]> FolderMessageIndices { get; }

    public int TotalMessageCount => RemoteMessageIds.Length;

    public IEnumerable<string> KnownRemoteFolderIds => FolderMessageIndices.Keys;

    public static IntelligenceCoverageInventory Empty(Guid localAccountId)
        => new(localAccountId, [], [], new Dictionary<string, int[]>(StringComparer.Ordinal), DateTimeOffset.UtcNow);

    public int[] GetFolderIndices(string remoteFolderId)
        => FolderMessageIndices.TryGetValue(remoteFolderId, out var indices) ? indices : [];

    public bool TryGetMessageIndex(string remoteMessageId, out int index)
        => _indicesByRemoteMessageId.TryGetValue(remoteMessageId, out index);

    public DateTimeOffset GetReceivedAt(int index)
        => new(ReceivedAtUtcTicks[index], TimeSpan.Zero);

    /// <summary>
    /// Builds the inventory from raw (message identity, received date, folder) rows. Rows for the
    /// same message in several folders collapse into one entry with several folder memberships.
    /// </summary>
    public static IntelligenceCoverageInventory Create(
        Guid localAccountId,
        IEnumerable<IntelligenceCoverageInventoryRow> rows)
    {
        var latestByMessage = new Dictionary<string, long>(StringComparer.Ordinal);
        var foldersByMessage = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.RemoteMessageId))
                continue;

            var ticks = row.ReceivedAtUtc.UtcTicks;
            if (!latestByMessage.TryGetValue(row.RemoteMessageId, out var existing) || ticks > existing)
                latestByMessage[row.RemoteMessageId] = ticks;

            if (string.IsNullOrEmpty(row.RemoteFolderId))
                continue;
            if (!foldersByMessage.TryGetValue(row.RemoteMessageId, out var folders))
                foldersByMessage[row.RemoteMessageId] = folders = new HashSet<string>(StringComparer.Ordinal);
            folders.Add(row.RemoteFolderId);
        }

        if (latestByMessage.Count == 0)
            return Empty(localAccountId);

        var ordered = latestByMessage
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToArray();

        var remoteMessageIds = new string[ordered.Length];
        var receivedAtUtcTicks = new long[ordered.Length];
        var folderBuilders = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        for (var index = 0; index < ordered.Length; index++)
        {
            var (remoteMessageId, ticks) = (ordered[index].Key, ordered[index].Value);
            remoteMessageIds[index] = remoteMessageId;
            receivedAtUtcTicks[index] = ticks;

            if (!foldersByMessage.TryGetValue(remoteMessageId, out var folders))
                continue;
            foreach (var folder in folders)
            {
                if (!folderBuilders.TryGetValue(folder, out var builder))
                    folderBuilders[folder] = builder = [];
                builder.Add(index);
            }
        }

        // Each list is appended in ascending index order, which is already newest-first.
        var folderMessageIndices = folderBuilders.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToArray(),
            StringComparer.Ordinal);

        return new IntelligenceCoverageInventory(
            localAccountId, remoteMessageIds, receivedAtUtcTicks, folderMessageIndices, DateTimeOffset.UtcNow);
    }
}

/// <summary>One (message, folder) membership as read from the mail database.</summary>
public readonly record struct IntelligenceCoverageInventoryRow(
    string RemoteMessageId,
    DateTimeOffset ReceivedAtUtc,
    string RemoteFolderId);
