using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Extensions;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.Controls.Core;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels.Collections;

/// <summary>
/// Serialized, flat storage for the active mail list. UI projection, grouping,
/// threading, expansion, and selection are owned by WinoMailListView.
/// </summary>
/// <remarks>
/// The store is not ordered. Pages are appended and live additions go to the end; the
/// projection sorts. Every mutation is serialized through one gate and applied on the UI
/// thread. Reads (<see cref="Find"/>, <see cref="ContainsMailUniqueId"/>,
/// <see cref="ContainsThreadKey"/>, <see cref="ItemIds"/>) are served from indexes that are
/// maintained inside the mutation and guarded by a lock, so messenger threads can query the
/// list without a dispatcher hop and without racing the UI thread.
/// </remarks>
public sealed class MailListStore
{
    /// <summary>
    /// An in-place update on the instance the list already holds cannot be diffed, so the view
    /// model refreshes every property. The keys that would force the projection to re-sort and
    /// reconcile every row are left out: a message does not change its date, thread or account
    /// while it stays the same instance, pin changes always arrive with an explicit hint, and a
    /// rebuild for a read-state change is pure cost.
    /// </summary>
    private const MailCopyChangeFlags SameInstanceRefreshFlags =
        MailCopyChangeFlags.All &
        ~(MailCopyChangeFlags.CreationDate |
          MailCopyChangeFlags.IsPinned |
          MailCopyChangeFlags.ThreadId |
          MailCopyChangeFlags.AssignedAccount |
          MailCopyChangeFlags.AssignedFolder |
          MailCopyChangeFlags.FromName |
          MailCopyChangeFlags.FromAddress);

    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly object _indexLock = new();
    private readonly Dictionary<Guid, MailItemViewModel> _itemsById = [];
    private readonly Dictionary<string, int> _threadKeyCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<LogicalMessageKey, List<MailItemViewModel>> _logicalIndex = [];

    public MailListStore()
    {
        Items.CollectionChanged += OnItemsCollectionChanged;
    }

    public Func<MailCopy, MailItemViewModel> MailItemFactory { get; set; } =
        static mailCopy => new MailItemViewModel(mailCopy);

    public IDispatcher CoreDispatcher { get; set; }

    public MailListCollection<MailItemViewModel> Items { get; } = [];

    public int Count => Items.Count;

    /// <summary>Snapshot of the listed identities. Safe to call from any thread.</summary>
    public IReadOnlyCollection<Guid> ItemIds
    {
        get
        {
            lock (_indexLock)
            {
                return _itemsById.Keys.ToArray();
            }
        }
    }

    public Task ClearAsync() =>
        RunSerializedAsync(() => ExecuteUIThread(() =>
        {
            using (Items.DeferRefresh())
            {
                Items.Clear();
            }
        }));

    /// <summary>
    /// Whether any listed mail belongs to the conversation identified by a
    /// <see cref="MailConversationIdentity.ThreadKey"/>. O(1) and safe from any thread.
    /// </summary>
    public bool ContainsThreadKey(string threadKey)
    {
        if (string.IsNullOrWhiteSpace(threadKey))
        {
            return false;
        }

        lock (_indexLock)
        {
            return _threadKeyCounts.ContainsKey(threadKey);
        }
    }

    public bool ContainsMailUniqueId(Guid uniqueId)
    {
        lock (_indexLock)
        {
            return _itemsById.ContainsKey(uniqueId);
        }
    }

    public MailItemViewModel Find(Guid uniqueId)
    {
        lock (_indexLock)
        {
            return _itemsById.TryGetValue(uniqueId, out var item) ? item : null;
        }
    }

    public void UpdateAccountNicknamePosition(AccountNicknamePosition position)
    {
        foreach (var mailItem in Items)
        {
            mailItem.AccountNicknamePosition = position;
        }
    }

    public Task AddAsync(MailCopy addedItem) =>
        addedItem is null
            ? Task.CompletedTask
            : RunSerializedAsync(() => ExecuteUIThread(() =>
            {
                using (Items.DeferRefresh())
                {
                    if (Items.TryGetItem(addedItem.UniqueId, out MailItemViewModel existing))
                    {
                        UpdateExisting(existing, addedItem);
                        return;
                    }

                    Items.TryAdd(MailItemFactory(addedItem));
                }
            }));

    /// <summary>
    /// Adds mail received through live synchronization without representing the same
    /// server message more than once when providers expose one message through multiple folders.
    /// </summary>
    public Task AddLiveAsync(
        MailCopy addedItem,
        EntityUpdateSource source,
        Func<MailCopy, bool> isPreferred) =>
        AddLiveRangeAsync(
            addedItem is null ? Array.Empty<MailCopy>() : (MailCopy[])[addedItem],
            source,
            isPreferred);

    public Task AddLiveRangeAsync(
        IEnumerable<MailCopy> addedItems,
        EntityUpdateSource source,
        Func<MailCopy, bool> isPreferred)
    {
        var incoming = DeduplicateById(addedItems);
        if (incoming.Length == 0)
        {
            return Task.CompletedTask;
        }

        return RunSerializedAsync(() => ExecuteUIThread(() =>
        {
            using (Items.DeferRefresh())
            {
                foreach (var mailCopy in incoming)
                {
                    MergeLiveMail(mailCopy, source, isPreferred);
                }
            }
        }));
    }

    /// <summary>
    /// Replaces the whole list with a new page in a single UI-thread pass. Used by folder,
    /// filter, sorting and search loads so a switch costs one dispatcher hop and one
    /// collection reset instead of a clear followed by a refill.
    /// </summary>
    public Task ResetAsync(
        IEnumerable<MailItemViewModel> items,
        Func<bool> shouldApply = null)
    {
        // Deduplication happens on the calling (background) thread. Only the collection
        // mutation itself needs the UI thread.
        var incoming = Deduplicate(items);

        return RunSerializedAsync(() => ExecuteUIThread(() =>
        {
            if (shouldApply?.Invoke() == false)
            {
                return;
            }

            using (Items.DeferRefresh())
            {
                Items.ReplaceAll(incoming);
            }

            MailListLoadTrace.MarkCurrent(MailListLoadStage.StoreApplied);
        }));
    }

    /// <summary>
    /// Appends a page. Gmail copies are merged by server identity so a label copy of a
    /// message that is already listed does not produce a second row. When
    /// <paramref name="isPreferred"/> is given, a listed copy that matches the active seed is
    /// kept even if the page brings a copy with a better rank, which keeps realized rows and
    /// the selection stable while the user scrolls.
    /// </summary>
    public Task AddRangeAsync(
        IEnumerable<MailItemViewModel> items,
        bool clearIdCache,
        Func<bool> shouldApply = null,
        Func<MailCopy, bool> isPreferred = null)
    {
        var incoming = Deduplicate(items);

        return RunSerializedAsync(() => ExecuteUIThread(() =>
        {
            if (shouldApply?.Invoke() == false)
            {
                return;
            }

            using (Items.DeferRefresh())
            {
                if (clearIdCache)
                {
                    Items.ReplaceAll(incoming);
                }
                else
                {
                    var additions = new List<MailItemViewModel>(incoming.Length);
                    foreach (var item in incoming)
                    {
                        if (item.MailCopy.AssignedAccount?.ProviderType == MailProviderType.Gmail)
                        {
                            MergeLiveMail(item.MailCopy, EntityUpdateSource.Server, isPreferred, item);
                            continue;
                        }

                        if (Items.TryGetItem(item.UniqueId, out MailItemViewModel existing))
                        {
                            UpdateExisting(existing, item.MailCopy);
                        }
                        else
                        {
                            additions.Add(item);
                        }
                    }

                    Items.AddRange(additions);
                }
            }

            MailListLoadTrace.MarkCurrent(MailListLoadStage.StoreApplied);
        }));
    }

    public Task UpdateThumbnailsForAddressAsync(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return Task.CompletedTask;
        }

        return RunSerializedAsync(() => ExecuteUIThread(() =>
        {
            foreach (var mailItem in Items)
            {
                if (mailItem.FromAddress?.Equals(address, StringComparison.OrdinalIgnoreCase) == true)
                {
                    mailItem.ThumbnailUpdatedEvent = !mailItem.ThumbnailUpdatedEvent;
                }
            }
        }));
    }

    public Task UpdateMailCopy(
        MailCopy updatedMailCopy,
        EntityUpdateSource mailUpdateSource,
        MailCopyChangeFlags changedProperties = MailCopyChangeFlags.None)
    {
        if (updatedMailCopy is null || !ContainsMailUniqueId(updatedMailCopy.UniqueId))
        {
            return Task.CompletedTask;
        }

        return RunSerializedAsync(() => ExecuteUIThread(() =>
        {
            if (!Items.TryGetItem(updatedMailCopy.UniqueId, out MailItemViewModel existing))
            {
                return;
            }

            using (Items.DeferRefresh())
            {
                UpdateExisting(existing, updatedMailCopy, changedProperties);
                existing.IsBusy = mailUpdateSource == EntityUpdateSource.ClientUpdated;
            }
        }));
    }

    public Task UpdateMailCopiesAsync(
        IEnumerable<MailCopy> updatedMailCopies,
        EntityUpdateSource mailUpdateSource,
        MailCopyChangeFlags changedProperties = MailCopyChangeFlags.None)
    {
        var copies = DeduplicateById(updatedMailCopies);
        if (copies.Length == 0)
        {
            return Task.CompletedTask;
        }

        return RunSerializedAsync(() => ExecuteUIThread(() =>
        {
            using (Items.DeferRefresh())
            {
                foreach (var copy in copies)
                {
                    if (!Items.TryGetItem(copy.UniqueId, out MailItemViewModel existing))
                    {
                        continue;
                    }

                    UpdateExisting(existing, copy, changedProperties);
                    existing.IsBusy = mailUpdateSource == EntityUpdateSource.ClientUpdated;
                }
            }
        }));
    }

    public Task UpdateMailStateAsync(
        MailStateChange updatedState,
        EntityUpdateSource mailUpdateSource)
    {
        if (updatedState is null || !ContainsMailUniqueId(updatedState.UniqueId))
        {
            return Task.CompletedTask;
        }

        return RunSerializedAsync(() => ExecuteUIThread(() =>
        {
            if (!Items.TryGetItem(updatedState.UniqueId, out MailItemViewModel existing))
            {
                return;
            }

            existing.ApplyStateChanges(updatedState.IsRead, updatedState.IsFlagged);
            existing.IsBusy = mailUpdateSource == EntityUpdateSource.ClientUpdated;
        }));
    }

    public Task UpdateMailStatesAsync(
        IEnumerable<MailStateChange> updatedStates,
        EntityUpdateSource mailUpdateSource)
    {
        var states = updatedStates?
            .Where(static state => state is not null)
            .GroupBy(static state => state.UniqueId)
            .Select(static group => group.Last())
            .ToArray() ?? [];
        if (states.Length == 0)
        {
            return Task.CompletedTask;
        }

        return RunSerializedAsync(() => ExecuteUIThread(() =>
        {
            foreach (var state in states)
            {
                if (!Items.TryGetItem(state.UniqueId, out MailItemViewModel existing))
                {
                    continue;
                }

                existing.ApplyStateChanges(state.IsRead, state.IsFlagged);
                existing.IsBusy = mailUpdateSource == EntityUpdateSource.ClientUpdated;
            }
        }));
    }

    public MailItemViewModel GetFirst() => Items.Count > 0 ? Items[0] : null;

    /// <summary>
    /// Removes mail copies and, for Gmail, promotes another label copy of the same server
    /// message when one survives the removal. Replacement candidates are resolved on the
    /// calling thread; only the collection mutation runs on the UI thread.
    /// </summary>
    public Task RemoveLiveRangeAsync(
        IEnumerable<MailCopy> removedItems,
        IEnumerable<MailCopy> survivingCopies,
        Func<MailCopy, bool> isPreferred)
    {
        // Only mail that is actually listed is removed and replaced; a copy that never made it
        // into this list must not pull its label siblings in.
        var removed = removedItems?
            .Where(mail => mail is not null && ContainsMailUniqueId(mail.UniqueId))
            .ToArray() ?? [];
        if (removed.Length == 0)
        {
            return Task.CompletedTask;
        }

        var removedIds = new HashSet<Guid>(removed.Length);
        foreach (var mail in removed)
        {
            removedIds.Add(mail.UniqueId);
        }

        var survivors = survivingCopies?
            .Where(mail => mail is not null && !removedIds.Contains(mail.UniqueId))
            .ToArray() ?? [];

        var replacements = new List<MailCopy>(removed.Length);
        foreach (var mail in removed)
        {
            var replacement = SelectReplacement(mail, survivors, isPreferred);
            if (replacement is not null)
            {
                replacements.Add(replacement);
            }
        }

        return RunSerializedAsync(() => ExecuteUIThread(() =>
        {
            using (Items.DeferRefresh())
            {
                Items.RemoveRangeById(removedIds);
                foreach (var replacement in replacements)
                {
                    MergeLiveMail(replacement, EntityUpdateSource.Server, isPreferred);
                }
            }
        }));
    }

    public Task RemoveAsync(MailCopy removeItem) =>
        removeItem is null
            ? Task.CompletedTask
            : RemoveRangeByIdAsync([removeItem.UniqueId]);

    public Task RemoveRangeAsync(IEnumerable<MailCopy> removeItems) =>
        RemoveRangeByIdAsync(removeItems?
            .Where(static item => item is not null)
            .Select(static item => item.UniqueId) ?? []);

    public Task RemoveRangeByIdAsync(IEnumerable<Guid> uniqueIds)
    {
        var ids = uniqueIds as IReadOnlySet<Guid> ?? uniqueIds?.ToHashSet() ?? [];
        if (ids.Count == 0)
        {
            return Task.CompletedTask;
        }

        return RunSerializedAsync(() => ExecuteUIThread(() =>
        {
            using (Items.DeferRefresh())
            {
                Items.RemoveRangeById(ids);
            }
        }));
    }

    public void Cleanup()
    {
        // The store has no messenger or item-level subscriptions.
    }

    private static MailCopy[] DeduplicateById(IEnumerable<MailCopy> copies)
    {
        if (copies is null)
        {
            return [];
        }

        // Last copy per identity wins, first occurrence keeps its position.
        var positions = new Dictionary<Guid, int>();
        var result = new List<MailCopy>();
        foreach (var copy in copies)
        {
            if (copy is null)
            {
                continue;
            }

            if (positions.TryGetValue(copy.UniqueId, out var position))
            {
                result[position] = copy;
            }
            else
            {
                positions.Add(copy.UniqueId, result.Count);
                result.Add(copy);
            }
        }

        return result.ToArray();
    }

    private static MailItemViewModel[] Deduplicate(IEnumerable<MailItemViewModel> items)
    {
        if (items is null)
        {
            return [];
        }

        // Pass one: last view model per identity wins, first occurrence keeps its position.
        var positionsById = new Dictionary<Guid, int>();
        var byId = new List<MailItemViewModel>();
        foreach (var item in items)
        {
            if (item is null)
            {
                continue;
            }

            if (positionsById.TryGetValue(item.UniqueId, out var position))
            {
                byId[position] = item;
            }
            else
            {
                positionsById.Add(item.UniqueId, byId.Count);
                byId.Add(item);
            }
        }

        // Pass two: one row per server message, keeping the most stable copy.
        var positionsByMessage = new Dictionary<(Guid, string, Guid), int>();
        var result = new List<MailItemViewModel>(byId.Count);
        foreach (var item in byId)
        {
            var key = MailConversationIdentity.MessageKey(item.MailCopy);
            if (!positionsByMessage.TryGetValue(key, out var position))
            {
                positionsByMessage.Add(key, result.Count);
                result.Add(item);
                continue;
            }

            var current = result[position];
            var currentRank = MailConversationIdentity.CopyRank(current.MailCopy);
            var candidateRank = MailConversationIdentity.CopyRank(item.MailCopy);
            if (candidateRank < currentRank ||
                (candidateRank == currentRank && item.UniqueId.CompareTo(current.UniqueId) < 0))
            {
                result[position] = item;
            }
        }

        return result.ToArray();
    }

    private static MailCopy SelectReplacement(
        MailCopy removed,
        MailCopy[] survivors,
        Func<MailCopy, bool> isPreferred)
    {
        MailCopy best = null;
        var bestPreferred = false;
        var bestRank = int.MaxValue;
        foreach (var candidate in survivors)
        {
            if (!HasSameLogicalIdentity(removed, candidate))
            {
                continue;
            }

            var preferred = isPreferred?.Invoke(candidate) ?? true;
            if (!preferred)
            {
                continue;
            }

            var rank = MailConversationIdentity.CopyRank(candidate);
            if (best is null ||
                (preferred && !bestPreferred) ||
                (preferred == bestPreferred &&
                 (rank < bestRank || (rank == bestRank && candidate.UniqueId.CompareTo(best.UniqueId) < 0))))
            {
                best = candidate;
                bestPreferred = preferred;
                bestRank = rank;
            }
        }

        return best;
    }

    /// <summary>
    /// Merges one live copy into the list. <paramref name="prebuilt"/> is the view model to add
    /// when the copy is new, so a page can reuse instances it already created.
    /// </summary>
    private void MergeLiveMail(
        MailCopy incoming,
        EntityUpdateSource source,
        Func<MailCopy, bool> isPreferred,
        MailItemViewModel prebuilt = null)
    {
        var logicalMatches = FindLogicalMatches(incoming);

        if (logicalMatches.Count == 0)
        {
            if (Items.TryGetItem(incoming.UniqueId, out MailItemViewModel existing))
            {
                UpdateExisting(existing, incoming);
                existing.IsBusy = source == EntityUpdateSource.ClientUpdated;
                return;
            }

            var added = prebuilt ?? MailItemFactory(incoming);
            added.IsBusy = source == EntityUpdateSource.ClientUpdated;
            Items.TryAdd(added);
            return;
        }

        MailItemViewModel preferredExisting = null;
        if (isPreferred is not null)
        {
            foreach (var match in logicalMatches)
            {
                if (isPreferred(match.MailCopy))
                {
                    preferredExisting = match;
                    break;
                }
            }
        }

        var shouldUseIncoming = preferredExisting is null &&
            (isPreferred?.Invoke(incoming) == true || OutranksEveryMatch(incoming, logicalMatches));

        if (shouldUseIncoming)
        {
            Items.RemoveRangeById(logicalMatches.Select(static item => item.UniqueId).ToHashSet());

            var added = prebuilt ?? MailItemFactory(incoming);
            added.IsBusy = source == EntityUpdateSource.ClientUpdated;
            Items.TryAdd(added);
            return;
        }

        var retained = preferredExisting;
        if (retained is null)
        {
            foreach (var match in logicalMatches)
            {
                if (match.UniqueId == incoming.UniqueId)
                {
                    retained = match;
                    break;
                }
            }

            retained ??= logicalMatches[0];
        }

        if (retained.UniqueId == incoming.UniqueId)
        {
            UpdateExisting(retained, incoming);
        }
        else
        {
            retained.ApplyStateChanges(incoming.IsRead, incoming.IsFlagged);
            retained.IsPinned = incoming.IsPinned;
            retained.IsFocused = incoming.IsFocused;
        }

        retained.IsBusy = source == EntityUpdateSource.ClientUpdated;

        if (logicalMatches.Count > 1)
        {
            var extraIds = new HashSet<Guid>();
            foreach (var match in logicalMatches)
            {
                if (match.UniqueId != retained.UniqueId)
                {
                    extraIds.Add(match.UniqueId);
                }
            }

            Items.RemoveRangeById(extraIds);
        }
    }

    private static bool OutranksEveryMatch(MailCopy incoming, List<MailItemViewModel> matches)
    {
        var incomingRank = MailConversationIdentity.CopyRank(incoming);
        foreach (var match in matches)
        {
            if (incomingRank >= MailConversationIdentity.CopyRank(match.MailCopy))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Applies an update to a listed view model and keeps the indexes in step when the
    /// update moves the mail to another conversation or server identity.
    /// </summary>
    private void UpdateExisting(
        MailItemViewModel existing,
        MailCopy source,
        MailCopyChangeFlags changeHint = MailCopyChangeFlags.None)
    {
        if (changeHint == MailCopyChangeFlags.None && ReferenceEquals(existing.MailCopy, source))
        {
            changeHint = SameInstanceRefreshFlags;
        }

        var previousThreadKey = existing.ThreadKey;
        var previousLogicalKey = LogicalMessageKey.For(existing.MailCopy);

        existing.UpdateFrom(source, changeHint);

        var threadKey = existing.ThreadKey;
        var logicalKey = LogicalMessageKey.For(existing.MailCopy);
        if (string.Equals(previousThreadKey, threadKey, StringComparison.Ordinal) &&
            previousLogicalKey == logicalKey)
        {
            return;
        }

        lock (_indexLock)
        {
            RemoveFromSecondaryIndexes(existing, previousThreadKey, previousLogicalKey);
            AddToSecondaryIndexes(existing, threadKey, logicalKey);
        }
    }

    private List<MailItemViewModel> FindLogicalMatches(MailCopy incoming)
    {
        var key = LogicalMessageKey.For(incoming);
        if (key == default)
        {
            return [];
        }

        lock (_indexLock)
        {
            return _logicalIndex.TryGetValue(key, out var matches)
                ? new List<MailItemViewModel>(matches)
                : [];
        }
    }

    private static bool HasSameLogicalIdentity(MailCopy left, MailCopy right)
    {
        var leftKey = LogicalMessageKey.For(left);
        return leftKey != default && leftKey == LogicalMessageKey.For(right);
    }

    private void OnItemsCollectionChanged(object sender, NotifyCollectionChangedEventArgs args)
    {
        lock (_indexLock)
        {
            switch (args.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    IndexItems(args.NewItems);
                    break;
                case NotifyCollectionChangedAction.Remove:
                    UnindexItems(args.OldItems);
                    break;
                case NotifyCollectionChangedAction.Replace:
                    UnindexItems(args.OldItems);
                    IndexItems(args.NewItems);
                    break;
                case NotifyCollectionChangedAction.Move:
                    break;
                default:
                    _itemsById.Clear();
                    _threadKeyCounts.Clear();
                    _logicalIndex.Clear();
                    foreach (var item in Items)
                    {
                        IndexItem(item);
                    }

                    break;
            }
        }
    }

    private void IndexItems(System.Collections.IList items)
    {
        if (items is null)
        {
            return;
        }

        foreach (var entry in items)
        {
            if (entry is MailItemViewModel item)
            {
                IndexItem(item);
            }
        }
    }

    private void UnindexItems(System.Collections.IList items)
    {
        if (items is null)
        {
            return;
        }

        foreach (var entry in items)
        {
            if (entry is MailItemViewModel item)
            {
                _itemsById.Remove(item.UniqueId);
                RemoveFromSecondaryIndexes(item, item.ThreadKey, LogicalMessageKey.For(item.MailCopy));
            }
        }
    }

    private void IndexItem(MailItemViewModel item)
    {
        _itemsById[item.UniqueId] = item;
        AddToSecondaryIndexes(item, item.ThreadKey, LogicalMessageKey.For(item.MailCopy));
    }

    private void AddToSecondaryIndexes(MailItemViewModel item, string threadKey, LogicalMessageKey logicalKey)
    {
        if (!string.IsNullOrEmpty(threadKey))
        {
            _threadKeyCounts[threadKey] = _threadKeyCounts.TryGetValue(threadKey, out var count) ? count + 1 : 1;
        }

        if (logicalKey != default)
        {
            if (!_logicalIndex.TryGetValue(logicalKey, out var matches))
            {
                matches = new List<MailItemViewModel>(1);
                _logicalIndex.Add(logicalKey, matches);
            }

            if (!matches.Contains(item))
            {
                matches.Add(item);
            }
        }
    }

    private void RemoveFromSecondaryIndexes(MailItemViewModel item, string threadKey, LogicalMessageKey logicalKey)
    {
        if (!string.IsNullOrEmpty(threadKey) && _threadKeyCounts.TryGetValue(threadKey, out var count))
        {
            if (count <= 1)
            {
                _threadKeyCounts.Remove(threadKey);
            }
            else
            {
                _threadKeyCounts[threadKey] = count - 1;
            }
        }

        if (logicalKey != default && _logicalIndex.TryGetValue(logicalKey, out var matches))
        {
            matches.Remove(item);
            if (matches.Count == 0)
            {
                _logicalIndex.Remove(logicalKey);
            }
        }
    }

    private Task ExecuteUIThread(Action action)
    {
        if (CoreDispatcher is null)
        {
            action();
            return Task.CompletedTask;
        }

        return CoreDispatcher.ExecuteOnUIThread(action);
    }

    private async Task RunSerializedAsync(Func<Task> action)
    {
        await _mutationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>
    /// Identity of a Gmail server message within one account. Label copies of the same message
    /// share this key; every other provider yields <see langword="default"/> because its copies
    /// are folder-scoped and never merge.
    /// </summary>
    private readonly record struct LogicalMessageKey(Guid AccountId, string MessageId)
    {
        public static LogicalMessageKey For(MailCopy mail)
        {
            if (mail is null ||
                mail.AssignedAccount?.ProviderType != MailProviderType.Gmail ||
                string.IsNullOrWhiteSpace(mail.Id))
            {
                return default;
            }

            var accountId = MailConversationIdentity.AccountId(mail);
            return accountId == Guid.Empty ? default : new LogicalMessageKey(accountId, mail.Id);
        }
    }
}
