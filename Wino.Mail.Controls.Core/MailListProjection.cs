using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;

namespace Wino.Mail.Controls.Core;

/// <summary>
/// Projects a flat source into contiguous single/thread row blocks and groups.
/// It is UI-framework independent and preserves expanded thread identity.
/// </summary>
public sealed partial class MailListProjection : IDisposable
{
    private readonly ObservableCollection<MailListGroup> _groups = [];
    private readonly Dictionary<Guid, MailListRow> _rowsByItemId = [];
    private readonly Dictionary<string, MailListThread> _threadsByKey = new(StringComparer.Ordinal);
    private readonly HashSet<string> _expandedThreadKeys = new(StringComparer.Ordinal);
    private readonly HashSet<IMailListSourceItem> _subscribedItems =
        new(ReferenceEqualityComparer.Instance);
    private IMailListCollection _source;
    private MailListProjectionOptions _options;
    private bool _isDisposed;
    private bool _isRebuildPending;

    public MailListProjection(
        IMailListCollection source,
        MailListProjectionOptions? options = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _options = options ?? new MailListProjectionOptions();
        Attach();
        Rebuild();
    }

    public event EventHandler? ProjectionChanging;

    public event EventHandler? ProjectionChanged;

    public event EventHandler<ThreadExpansionChangedEventArgs>? ThreadExpansionChanged;

    public ObservableCollection<MailListGroup> Groups => _groups;

    public IReadOnlyCollection<string> ExpandedThreadKeys => _expandedThreadKeys;

    public IEnumerable<MailListRow> Rows => _groups.SelectMany(static group => group);

    public IEnumerable<IMailListSourceItem> Items => _source;

    public IEnumerable<MailListThread> Threads => _threadsByKey.Values;

    public void SetOptions(MailListProjectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (_options == options)
        {
            return;
        }

        _options = options;
        Rebuild();
    }

    public bool IsThreadExpanded(string threadKey) => _expandedThreadKeys.Contains(threadKey);

    public MailListRow? FindRow(Guid stableId) =>
        _rowsByItemId.TryGetValue(stableId, out var row) ? row : null;

    public IMailListSourceItem? FindItem(Guid stableId) =>
        _source.TryGetItem(stableId, out var item) ? item : null;

    public MailListThread? FindThread(string threadKey) =>
        _threadsByKey.TryGetValue(threadKey, out var thread) ? thread : null;

    public MailListThread? GetThreadForItem(Guid stableId)
    {
        var item = FindItem(stableId);
        return item?.ThreadKey is { Length: > 0 } key ? FindThread(key) : null;
    }

    public int GetVisibleRowIndex(MailListRow row)
    {
        var index = 0;
        foreach (var group in _groups)
        {
            var localIndex = group.IndexOf(row);
            if (localIndex >= 0)
            {
                return index + localIndex;
            }

            index += group.Count;
        }

        return -1;
    }

    public IMailListSourceItem? GetAdjacentVisibleItem(Guid stableId, int offset = 1)
    {
        if (offset == 0)
        {
            return FindItem(stableId);
        }

        var rows = Rows.ToArray();
        var index = Array.FindIndex(rows, row => row.SourceItem.StableId == stableId);
        if (index < 0)
        {
            return null;
        }

        var adjacentIndex = index + offset;
        return adjacentIndex >= 0 && adjacentIndex < rows.Length
            ? rows[adjacentIndex].SourceItem
            : null;
    }

    public void ExpandThread(string threadKey)
    {
        if (!_threadsByKey.TryGetValue(threadKey, out var thread) ||
            thread.Count < 2 ||
            !_expandedThreadKeys.Add(threadKey))
        {
            return;
        }

        var collapsed = _expandedThreadKeys
            .Where(key => !string.Equals(key, threadKey, StringComparison.Ordinal))
            .ToArray();
        foreach (var key in collapsed)
        {
            _expandedThreadKeys.Remove(key);
        }

        ProjectionChanging?.Invoke(this, EventArgs.Empty);
        foreach (var key in collapsed)
        {
            SetThreadExpansion(_threadsByKey[key], false);
        }

        SetThreadExpansion(thread, true);
        ProjectionChanged?.Invoke(this, EventArgs.Empty);

        foreach (var key in collapsed)
        {
            ThreadExpansionChanged?.Invoke(this, new(key, false));
        }

        ThreadExpansionChanged?.Invoke(this, new(threadKey, true));
    }

    public void CollapseThread(string threadKey)
    {
        if (!_threadsByKey.TryGetValue(threadKey, out var thread) ||
            !_expandedThreadKeys.Remove(threadKey))
        {
            return;
        }

        ProjectionChanging?.Invoke(this, EventArgs.Empty);
        SetThreadExpansion(thread, false);
        ProjectionChanged?.Invoke(this, EventArgs.Empty);
        ThreadExpansionChanged?.Invoke(this, new(threadKey, false));
    }

    private void SetThreadExpansion(MailListThread thread, bool isExpanded)
    {
        foreach (var group in _groups)
        {
            var headIndex = -1;
            for (var index = 0; index < group.Count; index++)
            {
                if (group[index].IsThreadHead &&
                    ReferenceEquals(group[index].Thread, thread))
                {
                    headIndex = index;
                    break;
                }
            }

            if (headIndex < 0)
            {
                continue;
            }

            thread.IsExpanded = isExpanded;
            group[headIndex].NotifyExpansionChanged();
            if (isExpanded)
            {
                var insertIndex = headIndex + 1;
                foreach (var item in thread.Items)
                {
                    var child = MailListRow.ThreadChild(thread, item);
                    group.Insert(insertIndex++, child);
                    _rowsByItemId[item.StableId] = child;
                }
            }
            else
            {
                while (headIndex + 1 < group.Count &&
                       group[headIndex + 1] is
                       {
                           IsThreadChild: true,
                           Thread: { } childThread,
                       } &&
                       ReferenceEquals(childThread, thread))
                {
                    var child = group[headIndex + 1];
                    group.RemoveAt(headIndex + 1);
                    _rowsByItemId.Remove(child.SourceItem.StableId);
                }

                _rowsByItemId[thread.RepresentativeItem.StableId] = group[headIndex];
            }

            return;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        Detach();
    }

    private void Attach()
    {
        _source.CollectionChanged += SourceCollectionChanged;
        _source.BatchUpdateCompleted += SourceBatchUpdateCompleted;
        foreach (var item in _source)
        {
            Subscribe(item);
        }
    }

    private void Detach()
    {
        _source.CollectionChanged -= SourceCollectionChanged;
        _source.BatchUpdateCompleted -= SourceBatchUpdateCompleted;
        foreach (var item in _subscribedItems.ToArray())
        {
            Unsubscribe(item);
        }
    }

    private void SourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        if (e.OldItems is not null)
        {
            foreach (IMailListSourceItem item in e.OldItems)
            {
                Unsubscribe(item);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (IMailListSourceItem item in e.NewItems)
            {
                Subscribe(item);
            }
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var item in _subscribedItems.ToArray())
            {
                Unsubscribe(item);
            }

            foreach (var item in _source)
            {
                Subscribe(item);
            }
        }

        RequestRebuild();
    }

    private void ItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) ||
            e.PropertyName is nameof(IMailListSourceItem.ThreadKey)
                or nameof(IMailListSourceItem.DateSortKey)
                or nameof(IMailListSourceItem.NameSortKey)
                or nameof(IMailListSourceItem.IsPinned))
        {
            RequestRebuild();
        }
    }

    private void SourceBatchUpdateCompleted(object? sender, EventArgs e)
    {
        if (_isDisposed || !_isRebuildPending)
        {
            return;
        }

        _isRebuildPending = false;
        Rebuild();
    }

    private void RequestRebuild()
    {
        if (_source.IsBatchUpdating)
        {
            _isRebuildPending = true;
            return;
        }

        _isRebuildPending = false;
        Rebuild();
    }

    private void Rebuild()
    {
        ProjectionChanging?.Invoke(this, EventArgs.Empty);
        RebuildCore();
        ProjectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildCore()
    {
        var sourceItems = _source.ToArray();
        var existingRows = Rows
            .GroupBy(GetRowIdentity)
            .ToDictionary(
                static group => group.Key,
                static group => group.First());
        var existingThreads = new Dictionary<string, MailListThread>(
            _threadsByKey,
            StringComparer.Ordinal);
        var rebuiltThreads = new Dictionary<string, MailListThread>(
            StringComparer.Ordinal);
        var blocks = CreateBlocks(
                sourceItems,
                existingRows,
                existingThreads,
                rebuiltThreads)
            .OrderBy(
                static block => block,
                Comparer<RowBlock>.Create(CompareBlocks))
            .ToList();

        _threadsByKey.Clear();
        foreach (var (key, thread) in rebuiltThreads)
        {
            _threadsByKey.Add(key, thread);
        }

        _expandedThreadKeys.RemoveWhere(key => !_threadsByKey.ContainsKey(key));
        var desiredGroups = blocks
            .GroupBy(GetGroupKey)
            .Select(group => new DesiredGroup(
                group.Key,
                group.SelectMany(static block => block.Rows).ToArray()))
            .ToArray();

        ReconcileGroups(desiredGroups);

        _rowsByItemId.Clear();
        foreach (var row in Rows)
        {
            _rowsByItemId[row.SourceItem.StableId] = row;
        }
    }

    private IEnumerable<RowBlock> CreateBlocks(
        IReadOnlyList<IMailListSourceItem> items,
        IReadOnlyDictionary<RowIdentity, MailListRow> existingRows,
        IReadOnlyDictionary<string, MailListThread> existingThreads,
        IDictionary<string, MailListThread> rebuiltThreads)
    {
        var consumed = new HashSet<Guid>();
        var threadItemsByKey = items
            .Where(item => !string.IsNullOrWhiteSpace(item.ThreadKey))
            .GroupBy(item => item.ThreadKey!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.DateSortKey).ToArray(),
                StringComparer.Ordinal);

        foreach (var item in items)
        {
            if (!consumed.Add(item.StableId))
            {
                continue;
            }

            if (!_options.IsThreadingEnabled || string.IsNullOrWhiteSpace(item.ThreadKey))
            {
                yield return RowBlock.ForSingle(
                    item,
                    GetOrCreateRow(
                        existingRows,
                        new(MailListRowKind.Single, item.StableId),
                        item,
                        null));
                continue;
            }

            var threadItems = threadItemsByKey[item.ThreadKey!];
            foreach (var threadItem in threadItems)
            {
                consumed.Add(threadItem.StableId);
            }

            if (threadItems.Length == 1)
            {
                yield return RowBlock.ForSingle(
                    threadItems[0],
                    GetOrCreateRow(
                        existingRows,
                        new(MailListRowKind.Single, threadItems[0].StableId),
                        threadItems[0],
                        null));
                continue;
            }

            if (_options.ThreadMessageOrder == ThreadMessageOrder.NewestFirst)
            {
                Array.Reverse(threadItems);
            }

            var threadKey = item.ThreadKey!;
            var thread = existingThreads.TryGetValue(threadKey, out var existingThread) &&
                         existingThread.Items.SequenceEqual(threadItems)
                ? existingThread
                : new MailListThread(threadKey, threadItems);
            thread.IsExpanded = _expandedThreadKeys.Contains(threadKey);
            rebuiltThreads.Add(thread.Key, thread);

            var rows = new List<MailListRow>
            {
                GetOrCreateRow(
                    existingRows,
                    new(MailListRowKind.ThreadHead, thread.RepresentativeItem.StableId),
                    thread.RepresentativeItem,
                    thread),
            };
            if (thread.IsExpanded)
            {
                rows.AddRange(thread.Items.Select(threadItem =>
                    GetOrCreateRow(
                        existingRows,
                        new(MailListRowKind.ThreadChild, threadItem.StableId),
                        threadItem,
                        thread)));
            }

            yield return new RowBlock(
                thread.RepresentativeItem,
                rows,
                threadItems.Max(static threadItem => threadItem.DateSortKey),
                threadItems.Any(static threadItem => threadItem.IsPinned));
        }
    }

    private int CompareBlocks(RowBlock left, RowBlock right)
    {
        if (_options.IsPinnedFirst)
        {
            var pinned = right.IsPinned.CompareTo(left.IsPinned);
            if (pinned != 0)
            {
                return pinned;
            }
        }

        return _options.SortMode switch
        {
            MailListSortMode.Name => StringComparer.CurrentCultureIgnoreCase.Compare(
                left.Representative.NameSortKey,
                right.Representative.NameSortKey),
            MailListSortMode.None => 0,
            _ => right.DateSortKey.CompareTo(left.DateSortKey),
        };
    }

    private object GetGroupKey(RowBlock block)
    {
        if (_options.GroupMode == MailListGroupMode.None)
        {
            return string.Empty;
        }

        if (_options.IsPinnedFirst && block.IsPinned)
        {
            return new MailListProjectionGroupKey(true, null);
        }

        object value = _options.GroupMode switch
        {
            MailListGroupMode.Name =>
                string.IsNullOrWhiteSpace(block.Representative.NameSortKey)
                    ? "#"
                    : block.Representative.NameSortKey[..1].ToUpper(CultureInfo.CurrentCulture),
            _ => block.DateSortKey.LocalDateTime.Date,
        };
        return new MailListProjectionGroupKey(false, value);
    }

    private void ReconcileGroups(IReadOnlyList<DesiredGroup> desiredGroups)
    {
        var targetGroups = new List<MailListGroup>(desiredGroups.Count);
        foreach (var desiredGroup in desiredGroups)
        {
            var existingGroup = _groups.FirstOrDefault(
                group => Equals(group.Key, desiredGroup.Key));
            targetGroups.Add(existingGroup ?? new MailListGroup(desiredGroup.Key, []));
        }

        for (var index = _groups.Count - 1; index >= 0; index--)
        {
            if (!targetGroups.Contains(_groups[index]))
            {
                _groups.RemoveAt(index);
            }
        }

        for (var index = 0; index < targetGroups.Count; index++)
        {
            var target = targetGroups[index];
            var currentIndex = _groups.IndexOf(target);
            if (currentIndex < 0)
            {
                _groups.Insert(index, target);
            }
            else if (currentIndex != index)
            {
                _groups.Move(currentIndex, index);
            }
        }

        var targetGroupByRow = desiredGroups
            .SelectMany((desiredGroup, index) =>
                desiredGroup.Rows.Select(row => (Row: row, Group: targetGroups[index])))
            .ToDictionary(static pair => pair.Row, static pair => pair.Group);
        foreach (var group in targetGroups)
        {
            for (var index = group.Count - 1; index >= 0; index--)
            {
                if (!targetGroupByRow.TryGetValue(group[index], out var targetGroup) ||
                    !ReferenceEquals(group, targetGroup))
                {
                    group.RemoveAt(index);
                }
            }
        }

        for (var index = 0; index < desiredGroups.Count; index++)
        {
            ReconcileRows(targetGroups[index], desiredGroups[index].Rows);
        }
    }

    private static void ReconcileRows(
        MailListGroup group,
        IReadOnlyList<MailListRow> desiredRows)
    {
        for (var index = 0; index < desiredRows.Count; index++)
        {
            var desiredRow = desiredRows[index];
            if (index < group.Count && ReferenceEquals(group[index], desiredRow))
            {
                continue;
            }

            var currentIndex = group.IndexOf(desiredRow);
            if (currentIndex < 0)
            {
                group.Insert(index, desiredRow);
            }
            else
            {
                group.Move(currentIndex, index);
            }
        }

        while (group.Count > desiredRows.Count)
        {
            group.RemoveAt(group.Count - 1);
        }
    }

    private static MailListRow GetOrCreateRow(
        IReadOnlyDictionary<RowIdentity, MailListRow> existingRows,
        RowIdentity identity,
        IMailListSourceItem item,
        MailListThread? thread)
    {
        if (existingRows.TryGetValue(identity, out var existingRow) &&
            ReferenceEquals(existingRow.SourceItem, item) &&
            ReferenceEquals(existingRow.Thread, thread))
        {
            return existingRow;
        }

        return identity.Kind switch
        {
            MailListRowKind.ThreadHead => MailListRow.ThreadHead(thread!),
            MailListRowKind.ThreadChild => MailListRow.ThreadChild(thread!, item),
            _ => MailListRow.Single(item),
        };
    }

    private static RowIdentity GetRowIdentity(MailListRow row) =>
        new(row.Kind, row.SourceItem.StableId);

    private sealed record RowBlock(
        IMailListSourceItem Representative,
        IReadOnlyList<MailListRow> Rows,
        DateTimeOffset DateSortKey,
        bool IsPinned)
    {
        public static RowBlock ForSingle(IMailListSourceItem item, MailListRow row) =>
            new(item, (MailListRow[])[row], item.DateSortKey, item.IsPinned);
    }

    private sealed record DesiredGroup(object Key, IReadOnlyList<MailListRow> Rows);

    private readonly record struct RowIdentity(MailListRowKind Kind, Guid StableId);

    private void Subscribe(IMailListSourceItem item)
    {
        if (_subscribedItems.Add(item))
        {
            item.PropertyChanged += ItemPropertyChanged;
        }
    }

    private void Unsubscribe(IMailListSourceItem item)
    {
        if (_subscribedItems.Remove(item))
        {
            item.PropertyChanged -= ItemPropertyChanged;
        }
    }
}
