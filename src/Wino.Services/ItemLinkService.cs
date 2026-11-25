using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.UI;

namespace Wino.Services;

/// <summary>
/// Stores <see cref="ItemLink"/> rows. Registered once per container: the links that wait for a create to be
/// confirmed live here, in memory, and are dropped with the process. They are not a queue; nothing is retried.
/// </summary>
public sealed class ItemLinkService(IDatabaseService databaseService, IMessenger messenger)
    : BaseDatabaseService(databaseService, messenger), IItemLinkService
{
    // A create that never reports back (failed, or the app closed) must not keep its links forever.
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromHours(6);

    private readonly object _pendingLock = new();
    private readonly Dictionary<Guid, List<PendingLink>> _pending = [];
    private readonly ILogger _logger = Log.ForContext<ItemLinkService>();

    public void ExpectLink(Guid pendingItemId, ItemLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (pendingItemId == Guid.Empty)
            throw new ArgumentException("A pending item id is required.", nameof(pendingItemId));

        var now = DateTime.UtcNow;
        lock (_pendingLock)
        {
            foreach (var expired in _pending.Where(entry => entry.Value.All(item => now - item.QueuedAtUtc > PendingLifetime)).Select(entry => entry.Key).ToList())
                _pending.Remove(expired);

            if (!_pending.TryGetValue(pendingItemId, out var links))
                _pending[pendingItemId] = links = [];

            links.Add(new PendingLink(Clone(link), now));
        }
    }

    public void DiscardPendingLinks(Guid pendingItemId)
    {
        lock (_pendingLock)
            _pending.Remove(pendingItemId);
    }

    public async Task ConfirmPendingLinksAsync(Guid pendingItemId, string confirmedKey)
    {
        if (string.IsNullOrWhiteSpace(confirmedKey))
            return;

        List<PendingLink> links;
        lock (_pendingLock)
        {
            if (!_pending.Remove(pendingItemId, out links))
                return;
        }

        var rows = links.Select(pending =>
        {
            var row = pending.Link;
            row.TargetKey = confirmedKey;
            row.CreatedAtUtc = DateTime.UtcNow;
            return row;
        }).ToList();

        try
        {
            await Connection.RunInTransactionAsync(transaction =>
            {
                foreach (var row in rows)
                    transaction.Insert(row, typeof(ItemLink));
            }).ConfigureAwait(false);
            _logger.Information("Stored {Count} item link(s) for confirmed {Kind}.", rows.Count, rows[0].TargetKind);
            ReportUIChange(new ItemLinksChanged(rows));
        }
        catch (Exception ex)
        {
            // The item itself is confirmed and stored; only the link is lost.
            _logger.Error(ex, "Could not store the links of a confirmed {Kind}.", rows[0].TargetKind);
        }
    }

    public async Task AddLinkAsync(ItemLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (string.IsNullOrWhiteSpace(link.SourceKey) || string.IsNullOrWhiteSpace(link.TargetKey))
            throw new ArgumentException("A link needs both keys.", nameof(link));

        await Connection.InsertAsync(link, typeof(ItemLink)).ConfigureAwait(false);
        ReportUIChange(new ItemLinksChanged([link]));
    }

    public async Task<List<ItemLink>> GetLinksAsync(ItemLinkKind kind, IReadOnlyCollection<string> keys)
    {
        var distinctKeys = DistinctKeys(keys);
        if (distinctKeys.Count == 0)
            return [];

        var result = new Dictionary<Guid, ItemLink>();
        foreach (var batch in SqliteVariableLimit.Batch(distinctKeys, parametersPerItem: 2, fixedParameters: 2))
        {
            var placeholders = string.Join(",", batch.Select(static _ => "?"));
            var parameters = new List<object> { (int)kind };
            parameters.AddRange(batch);
            parameters.Add((int)kind);
            parameters.AddRange(batch);

            var rows = await Connection.QueryAsync<ItemLink>(
                $"SELECT * FROM {nameof(ItemLink)} WHERE ({nameof(ItemLink.SourceKind)} = ? AND {nameof(ItemLink.SourceKey)} IN ({placeholders})) " +
                $"OR ({nameof(ItemLink.TargetKind)} = ? AND {nameof(ItemLink.TargetKey)} IN ({placeholders}))",
                parameters.ToArray()).ConfigureAwait(false);

            foreach (var row in rows)
                result[row.Id] = row;
        }

        return result.Values.OrderBy(static link => link.CreatedAtUtc).ToList();
    }

    public async Task DeleteLinksForItemAsync(ItemLinkKind kind, IReadOnlyCollection<string> keys)
    {
        var links = await GetLinksAsync(kind, keys).ConfigureAwait(false);
        if (links.Count == 0)
            return;

        foreach (var batch in SqliteVariableLimit.Batch(links.Select(static link => link.Id)))
        {
            var placeholders = string.Join(",", batch.Select(static _ => "?"));
            await Connection.ExecuteAsync(
                $"DELETE FROM {nameof(ItemLink)} WHERE {nameof(ItemLink.Id)} IN ({placeholders})",
                batch.Cast<object>().ToArray()).ConfigureAwait(false);
        }

        ReportUIChange(new ItemLinksChanged(links));
    }

    public async Task DeleteLinksForAccountAsync(Guid accountId)
    {
        var links = await Connection.QueryAsync<ItemLink>(
            $"SELECT * FROM {nameof(ItemLink)} WHERE {nameof(ItemLink.SourceAccountId)} = ? OR {nameof(ItemLink.TargetAccountId)} = ?",
            accountId,
            accountId).ConfigureAwait(false);
        if (links.Count == 0)
            return;

        await Connection.ExecuteAsync(
            $"DELETE FROM {nameof(ItemLink)} WHERE {nameof(ItemLink.SourceAccountId)} = ? OR {nameof(ItemLink.TargetAccountId)} = ?",
            accountId,
            accountId).ConfigureAwait(false);
        ReportUIChange(new ItemLinksChanged(links));
    }

    public async Task RekeyAsync(ItemLinkKind kind, string oldKey, string newKey)
    {
        if (string.IsNullOrWhiteSpace(oldKey) || string.IsNullOrWhiteSpace(newKey) || oldKey == newKey)
            return;

        await Connection.RunInTransactionAsync(transaction =>
        {
            transaction.Execute(
                $"UPDATE {nameof(ItemLink)} SET {nameof(ItemLink.SourceKey)} = ? WHERE {nameof(ItemLink.SourceKind)} = ? AND {nameof(ItemLink.SourceKey)} = ?",
                newKey, (int)kind, oldKey);
            transaction.Execute(
                $"UPDATE {nameof(ItemLink)} SET {nameof(ItemLink.TargetKey)} = ? WHERE {nameof(ItemLink.TargetKind)} = ? AND {nameof(ItemLink.TargetKey)} = ?",
                newKey, (int)kind, oldKey);
        }).ConfigureAwait(false);

        var links = await GetLinksAsync(kind, [newKey]).ConfigureAwait(false);
        if (links.Count > 0)
            ReportUIChange(new ItemLinksChanged(links));
    }

    private static List<string> DistinctKeys(IReadOnlyCollection<string> keys)
        => keys?
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];

    private static ItemLink Clone(ItemLink link) => new()
    {
        Id = link.Id == Guid.Empty ? Guid.NewGuid() : link.Id,
        SourceKind = link.SourceKind,
        SourceAccountId = link.SourceAccountId,
        SourceKey = link.SourceKey,
        TargetKind = link.TargetKind,
        TargetAccountId = link.TargetAccountId,
        TargetKey = link.TargetKey,
        Relation = link.Relation,
        SourceTitle = link.SourceTitle ?? string.Empty,
        SourceSubtitle = link.SourceSubtitle ?? string.Empty,
        TargetTitle = link.TargetTitle ?? string.Empty,
        CreatedAtUtc = link.CreatedAtUtc
    };

    private sealed record PendingLink(ItemLink Link, DateTime QueuedAtUtc);
}
