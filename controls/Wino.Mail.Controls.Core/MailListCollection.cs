using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
#if WINRT_EXPOSED
using WinRT;
#endif

namespace Wino.Mail.Controls.Core;

/// <summary>
/// Flat observable storage with stable-ID indexing. Projection, selection,
/// threading, dispatching, and domain mutations deliberately live elsewhere.
/// </summary>
#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial class MailListCollection<TItem> : ObservableCollection<TItem>, IMailListCollection
    where TItem : class, IMailListSourceItem
{
    private readonly Dictionary<Guid, TItem> _itemsById = [];
    private int _batchDepth;

    public IReadOnlyCollection<Guid> ItemIds => _itemsById.Keys;

    public bool IsBatchUpdating => _batchDepth > 0;

    public event EventHandler? BatchUpdateCompleted;

    public IDisposable DeferRefresh()
    {
        _batchDepth++;
        return new RefreshDeferral(this);
    }

    public bool ContainsId(Guid id) => _itemsById.ContainsKey(id);

    public bool TryGetItem(Guid id, out IMailListSourceItem? item)
    {
        var found = _itemsById.TryGetValue(id, out var typedItem);
        item = typedItem;
        return found;
    }

    public bool TryGetItem(Guid id, out TItem? item) => _itemsById.TryGetValue(id, out item);

    public bool TryAdd(TItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (ContainsId(item.StableId))
        {
            return false;
        }

        Add(item);
        return true;
    }

    public int AddRange(IEnumerable<TItem> items)
    {
        var additions = ValidateNewItems(items, allowExistingIds: true);
        if (additions.Count == 0)
        {
            return 0;
        }

        CheckReentrancy();
        foreach (var item in additions)
        {
            Items.Add(item);
            _itemsById.Add(item.StableId, item);
        }

        OnPropertyChanged(new(nameof(Count)));
        OnPropertyChanged(new("Item[]"));
        OnCollectionChanged(new(
            NotifyCollectionChangedAction.Add,
            (IList)additions,
            Items.Count - additions.Count));
        return additions.Count;
    }

    public bool RemoveById(Guid id)
    {
        if (!_itemsById.TryGetValue(id, out var item))
        {
            return false;
        }

        return Remove(item);
    }

    public int RemoveRangeById(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var idSet = ids.ToHashSet();
        if (idSet.Count == 0)
        {
            return 0;
        }

        var removed = Items.Where(item => idSet.Contains(item.StableId)).ToArray();
        if (removed.Length == 0)
        {
            return 0;
        }

        using (DeferRefresh())
        {
            foreach (var index in Enumerable
                         .Range(0, Items.Count)
                         .Where(index => idSet.Contains(Items[index].StableId))
                         .OrderDescending())
            {
                RemoveItem(index);
            }
        }

        return removed.Length;
    }

    public void ReplaceAll(IEnumerable<TItem> items)
    {
        var replacements = ValidateNewItems(items, allowExistingIds: false);

        CheckReentrancy();
        Items.Clear();
        _itemsById.Clear();
        foreach (var item in replacements)
        {
            Items.Add(item);
            _itemsById.Add(item.StableId, item);
        }

        OnPropertyChanged(new(nameof(Count)));
        OnPropertyChanged(new("Item[]"));
        OnCollectionChanged(new(NotifyCollectionChangedAction.Reset));
    }

    protected override void InsertItem(int index, TItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_itemsById.ContainsKey(item.StableId))
        {
            throw new ArgumentException($"An item with ID '{item.StableId}' already exists.", nameof(item));
        }

        base.InsertItem(index, item);
        _itemsById.Add(item.StableId, item);
    }

    protected override void SetItem(int index, TItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var current = Items[index];
        if (current.StableId != item.StableId && _itemsById.ContainsKey(item.StableId))
        {
            throw new ArgumentException($"An item with ID '{item.StableId}' already exists.", nameof(item));
        }

        base.SetItem(index, item);
        _itemsById.Remove(current.StableId);
        _itemsById[item.StableId] = item;
    }

    protected override void RemoveItem(int index)
    {
        var item = Items[index];
        base.RemoveItem(index);
        _itemsById.Remove(item.StableId);
    }

    protected override void ClearItems()
    {
        base.ClearItems();
        _itemsById.Clear();
    }

    IEnumerator<IMailListSourceItem> IEnumerable<IMailListSourceItem>.GetEnumerator() =>
        Items.Cast<IMailListSourceItem>().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => Items.GetEnumerator();

    private List<TItem> ValidateNewItems(IEnumerable<TItem> items, bool allowExistingIds)
    {
        ArgumentNullException.ThrowIfNull(items);
        var result = new List<TItem>();
        var seen = new HashSet<Guid>();

        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (!seen.Add(item.StableId))
            {
                throw new ArgumentException($"The input contains duplicate ID '{item.StableId}'.", nameof(items));
            }

            if (_itemsById.ContainsKey(item.StableId))
            {
                if (allowExistingIds)
                {
                    continue;
                }

                // ReplaceAll replaces the entire identity set, so existing IDs are valid.
            }

            result.Add(item);
        }

        return result;
    }

    private void EndDeferRefresh()
    {
        if (_batchDepth == 0)
        {
            return;
        }

        _batchDepth--;
        if (_batchDepth == 0)
        {
            BatchUpdateCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed partial class RefreshDeferral(MailListCollection<TItem> owner) : IDisposable
    {
        private MailListCollection<TItem>? _owner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.EndDeferRefresh();
        }
    }
}
