using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using MoreLinq.Extensions;
using Serilog;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels.Collections;

public class WinoMailCollection : ObservableRecipient, IRecipient<SelectedItemsChangedMessage>
{
    // We cache each mail copy id for faster access on updates.
    // If the item provider here for update or removal doesn't exist here
    // we can ignore the operation.

    public ConcurrentDictionary<Guid, bool> MailCopyIdHashSet = [];

    // Cache ThreadIds to quickly find items that should be threaded together
    private readonly ConcurrentDictionary<string, List<IMailListItem>> _threadIdToItemsMap = new();

    // Cache item to group mapping for faster lookups
    private readonly ConcurrentDictionary<IMailListItem, ObservableGroup<object, IMailListItem>> _itemToGroupMap = new();

    // Cache uniqueId to MailItemViewModel for faster GetMailItemContainer lookups
    private readonly ConcurrentDictionary<Guid, MailItemViewModel> _uniqueIdToMailItemMap = new();

    // Cache uniqueId to ThreadMailItemViewModel for O(1) thread membership checks
    private readonly ConcurrentDictionary<Guid, ThreadMailItemViewModel> _uniqueIdToThreadMap = new();

    public event EventHandler<MailItemViewModel> MailItemRemoved;
    public event EventHandler ItemSelectionChanged;
    public Func<string, ThreadMailItemViewModel> ThreadItemFactory { get; set; } = static threadId => new ThreadMailItemViewModel(threadId, true);

    private ListItemComparer listComparer = new();

    private readonly ObservableGroupedCollection<object, IMailListItem> _mailItemSource = new ObservableGroupedCollection<object, IMailListItem>();
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private int _selectionNotificationSuppressionCount;
    private bool _selectionNotificationPending;

    public ReadOnlyObservableGroupedCollection<object, IMailListItem> MailItems { get; }

    private SortingOptionType _sortingType;

    /// <summary>
    /// Property that defines how the item sorting should be done in the collection.
    /// </summary>
    public SortingOptionType SortingType
    {
        get => _sortingType;
        set
        {
            _sortingType = value;
            // Update the comparer's sort mode when sorting type changes
            listComparer.SortByName = value == SortingOptionType.Sender;
        }
    }

    /// <summary>
    /// Gets or sets the grouping type for emails.
    /// Note: WinoMailCollection groups automatically on the UI, so this just affects the grouping key logic.
    /// </summary>
    public EmailGroupingType GroupingType
    {
        get => SortingType == SortingOptionType.ReceiveDate ? EmailGroupingType.ByDate : EmailGroupingType.ByFromName;
        set => SortingType = value == EmailGroupingType.ByDate ? SortingOptionType.ReceiveDate : SortingOptionType.Sender;
    }

    /// <summary>
    /// Automatically deletes single mail items after the delete operation or thread->single transition.
    /// This is useful when reply draft is discarded in the thread. Only enabled for Draft folder for now.
    /// </summary>
    public bool PruneSingleNonDraftItems { get; set; }

    public int Count => _mailItemSource.Count;

    public bool IsAllSelected
    {
        get
        {
            return AllItemsCount == SelectedItemsCount;
        }
    }

    public IDispatcher CoreDispatcher { get; set; }

    public WinoMailCollection()
    {
        MailItems = new ReadOnlyObservableGroupedCollection<object, IMailListItem>(_mailItemSource);

        // Initialize sorting type to default (date-based)
        SortingType = SortingOptionType.ReceiveDate;

        Messenger.Register<SelectedItemsChangedMessage>(this);
    }

    public void Cleanup()
    {
        Messenger.Unregister<SelectedItemsChangedMessage>(this);
    }

    public async Task ClearAsync()
    {
        await RunSerializedAsync(async () =>
        {
            await ExecuteUIThread(() =>
            {
                foreach (var group in _mailItemSource)
                {
                    foreach (var item in group)
                    {
                        if (item is ThreadMailItemViewModel threadItem)
                        {
                            threadItem.UnregisterThreadEmailPropertyChangedHandlers();
                        }
                    }
                }

                _mailItemSource.Clear();
                MailCopyIdHashSet.Clear();
                _threadIdToItemsMap.Clear();
                _itemToGroupMap.Clear();
                _uniqueIdToMailItemMap.Clear();
                _uniqueIdToThreadMap.Clear();
            });
        });
    }

    private object GetGroupingKey(IMailListItem mailItem)
    {
        if (mailItem.IsPinned)
            return MailListGroupKey.Pinned;

        if (SortingType == SortingOptionType.ReceiveDate)
            return new MailListGroupKey(false, mailItem.CreationDate.ToLocalTime().Date);

        return new MailListGroupKey(false, mailItem.FromName);
    }

    private bool ShouldReinsertForChanges(MailCopyChangeFlags changedProperties)
    {
        if ((changedProperties & (MailCopyChangeFlags.ThreadId | MailCopyChangeFlags.IsPinned)) != 0)
            return true;

        if (SortingType == SortingOptionType.ReceiveDate)
            return (changedProperties & MailCopyChangeFlags.CreationDate) != 0;

        return (changedProperties & (MailCopyChangeFlags.FromName | MailCopyChangeFlags.FromAddress)) != 0;
    }

    private void UpdateUniqueIdHashes(IMailHashContainer itemContainer, bool isAdd)
    {
        if (isAdd)
        {
            if (itemContainer is MailItemViewModel mailItemVM)
            {
                MailCopyIdHashSet.TryAdd(mailItemVM.MailCopy.UniqueId, true);
                _uniqueIdToMailItemMap[mailItemVM.MailCopy.UniqueId] = mailItemVM;
            }
            else if (itemContainer is ThreadMailItemViewModel threadVM)
            {
                foreach (var email in threadVM.ThreadEmails)
                {
                    MailCopyIdHashSet.TryAdd(email.MailCopy.UniqueId, true);
                    _uniqueIdToMailItemMap[email.MailCopy.UniqueId] = email;
                    _uniqueIdToThreadMap[email.MailCopy.UniqueId] = threadVM;
                }
            }
        }
        else
        {
            foreach (var id in itemContainer.GetContainingIds())
            {
                MailCopyIdHashSet.TryRemove(id, out _);
                _uniqueIdToMailItemMap.TryRemove(id, out _);
                _uniqueIdToThreadMap.TryRemove(id, out _);
            }
        }
    }

    private void UpdateThreadIdCache(IMailListItem item, bool isAdd)
    {
        var threadIds = GetThreadIdsFromItem(item);

        foreach (var threadId in threadIds)
        {
            if (string.IsNullOrEmpty(threadId)) continue;

            if (isAdd)
            {
                var list = _threadIdToItemsMap.GetOrAdd(threadId, _ => new List<IMailListItem>());
                list.Add(item);
            }
            else
            {
                if (_threadIdToItemsMap.TryGetValue(threadId, out var list))
                {
                    list.Remove(item);
                    if (list.Count == 0)
                    {
                        _threadIdToItemsMap.TryRemove(threadId, out _);
                    }
                }
            }
        }
    }

    private IEnumerable<string> GetThreadIdsFromItem(IMailListItem item)
    {
        if (item is MailItemViewModel mailItem && !string.IsNullOrEmpty(mailItem.MailCopy.ThreadId))
        {
            yield return mailItem.MailCopy.ThreadId;
        }
        else if (item is ThreadMailItemViewModel threadItem)
        {
            var uniqueThreadIds = threadItem.ThreadEmails
                .Where(e => !string.IsNullOrEmpty(e.MailCopy.ThreadId))
                .Select(e => e.MailCopy.ThreadId)
                .Distinct();

            foreach (var threadId in uniqueThreadIds)
            {
                yield return threadId;
            }
        }
    }

    private IMailListItem FindThreadableItem(string threadId, Guid? excludedUniqueId = null, IMailListItem excludedItem = null)
    {
        if (string.IsNullOrEmpty(threadId) || !_threadIdToItemsMap.TryGetValue(threadId, out var items))
        {
            return null;
        }

        foreach (var item in items)
        {
            if (ReferenceEquals(item, excludedItem))
            {
                continue;
            }

            if (excludedUniqueId.HasValue)
            {
                if (item is MailItemViewModel mailItem && mailItem.MailCopy.UniqueId == excludedUniqueId.Value)
                {
                    continue;
                }

                if (item is ThreadMailItemViewModel threadItem && threadItem.HasUniqueId(excludedUniqueId.Value))
                {
                    continue;
                }
            }

            return item;
        }

        return null;
    }

    /// <summary>
    /// Checks if a ThreadId exists in the collection.
    /// </summary>
    /// <param name="threadId">The ThreadId to check for.</param>
    /// <returns>True if the ThreadId exists in the collection, false otherwise.</returns>
    public bool ContainsThreadId(string threadId)
    {
        return !string.IsNullOrEmpty(threadId) && _threadIdToItemsMap.ContainsKey(threadId);
    }

    /// <summary>
    /// Checks whether a mail with the given UniqueId currently exists in this collection.
    /// </summary>
    public bool ContainsMailUniqueId(Guid uniqueId) => MailCopyIdHashSet.ContainsKey(uniqueId);

    /// <summary>
    /// Finds a MailItemViewModel by its UniqueId, searching through all items including those inside threads.
    /// </summary>
    /// <param name="uniqueId">The UniqueId of the mail item to find.</param>
    /// <returns>The MailItemViewModel if found, otherwise null.</returns>
    public MailItemViewModel Find(Guid uniqueId)
    {
        // Fast path: check the cache for O(1) lookup
        if (_uniqueIdToMailItemMap.TryGetValue(uniqueId, out var cachedMailItem))
        {
            return cachedMailItem;
        }

        // Fallback: scan all groups and populate caches
        foreach (var group in _mailItemSource)
        {
            foreach (var item in group)
            {
                if (item is MailItemViewModel mailItem && mailItem.MailCopy.UniqueId == uniqueId)
                {
                    _uniqueIdToMailItemMap[uniqueId] = mailItem;
                    return mailItem;
                }
                else if (item is ThreadMailItemViewModel threadItem)
                {
                    var foundInThread = threadItem.ThreadEmails.FirstOrDefault(e => e.MailCopy.UniqueId == uniqueId);
                    if (foundInThread != null)
                    {
                        _uniqueIdToMailItemMap[uniqueId] = foundInThread;
                        _uniqueIdToThreadMap[uniqueId] = threadItem;
                        return foundInThread;
                    }
                }
            }
        }

        return null;
    }

    public ThreadMailItemViewModel GetThreadByMailUniqueId(Guid uniqueId)
    {
        if (_uniqueIdToThreadMap.TryGetValue(uniqueId, out var threadViewModel))
        {
            return threadViewModel;
        }

        _ = Find(uniqueId);

        return _uniqueIdToThreadMap.TryGetValue(uniqueId, out threadViewModel) ? threadViewModel : null;
    }

    public List<ThreadMailItemViewModel> GetThreadItems()
    {
        var threads = new List<ThreadMailItemViewModel>();

        foreach (var group in _mailItemSource)
        {
            foreach (var item in group)
            {
                if (item is ThreadMailItemViewModel threadItem)
                {
                    threads.Add(threadItem);
                }
            }
        }

        return threads;
    }

    private async Task InsertItemInternalAsync(object groupKey, IMailListItem mailItem)
    {
        UpdateUniqueIdHashes(mailItem, true);
        UpdateThreadIdCache(mailItem, true);
        await ExecuteUIThread(() =>
        {
            _mailItemSource.InsertItem(groupKey, listComparer, mailItem, listComparer);

            // Update item-to-group cache
            var group = _mailItemSource.FirstGroupByKeyOrDefault(groupKey);
            if (group != null)
            {
                _itemToGroupMap[mailItem] = group;
            }
        });
    }

    private async Task RemoveItemInternalAsync(ObservableGroup<object, IMailListItem> group, IMailListItem mailItem, bool detachThreadHandlers = true)
    {
        UpdateUniqueIdHashes(mailItem, false);
        UpdateThreadIdCache(mailItem, false);

        if (mailItem is MailItemViewModel singleMailItem)
        {
            MailItemRemoved?.Invoke(this, singleMailItem);
        }
        else if (mailItem is ThreadMailItemViewModel threadViewModel)
        {
            foreach (var threadMailItem in threadViewModel.ThreadEmails)
            {
                MailItemRemoved?.Invoke(this, threadMailItem);
            }

            if (detachThreadHandlers)
            {
                threadViewModel.UnregisterThreadEmailPropertyChangedHandlers();
            }
        }

        await ExecuteUIThread(() =>
        {
            group.Remove(mailItem);

            // Remove from item-to-group cache
            _itemToGroupMap.TryRemove(mailItem, out _);

            if (group.Count == 0)
            {
                _mailItemSource.RemoveGroup(group.Key);
            }
        });
    }

    private async Task HandleThreadingAsync(ObservableGroup<object, IMailListItem> group, IMailListItem item, MailCopy addedItem)
    {
        if (item is ThreadMailItemViewModel threadViewModel)
        {
            await HandleExistingThreadAsync(group, threadViewModel, addedItem);
        }
        else if (item is MailItemViewModel mailViewModel)
        {
            await HandleNewThreadAsync(group, mailViewModel, addedItem);
        }
    }

    private async Task HandleExistingThreadAsync(ObservableGroup<object, IMailListItem> group, ThreadMailItemViewModel threadViewModel, MailCopy addedItem)
    {
        var existingGroupKey = GetGroupingKey(threadViewModel);

        // Update ThreadId cache before modifying the thread
        UpdateThreadIdCache(threadViewModel, false);

        var newMailItem = new MailItemViewModel(addedItem);

        await ExecuteUIThread(() =>
        {
            threadViewModel.AddEmail(newMailItem);
        });

        // Update ThreadId cache after modifying the thread
        UpdateThreadIdCache(threadViewModel, true);

        // Update caches for the new mail item (use the actual instance, not a throwaway)
        MailCopyIdHashSet.TryAdd(addedItem.UniqueId, true);
        _uniqueIdToMailItemMap[addedItem.UniqueId] = newMailItem;
        _uniqueIdToThreadMap[addedItem.UniqueId] = threadViewModel;

        var newGroupKey = GetGroupingKey(threadViewModel);

        if (!existingGroupKey.Equals(newGroupKey))
        {
            await MoveThreadToNewGroupAsync(group, threadViewModel, newGroupKey);
        }
        else
        {
            await ExecuteUIThread(() => { threadViewModel.ThreadEmails = threadViewModel.ThreadEmails; });
        }
    }

    private async Task HandleNewThreadAsync(ObservableGroup<object, IMailListItem> group, MailItemViewModel item, MailCopy addedItem)
    {
        if (item.MailCopy.UniqueId == addedItem.UniqueId)
        {
            var existingItemContainer = GetMailItemContainer(addedItem.UniqueId);
            await UpdateExistingItemAsync(existingItemContainer, addedItem);
        }
        else
        {
            await CreateNewThreadAsync(group, item, addedItem);
        }
    }

    private async Task MoveThreadToNewGroupAsync(ObservableGroup<object, IMailListItem> currentGroup, ThreadMailItemViewModel threadViewModel, object newGroupKey)
    {
        await RemoveItemInternalAsync(currentGroup, threadViewModel, detachThreadHandlers: false);
        await InsertItemInternalAsync(newGroupKey, threadViewModel);
    }

    private async Task CreateNewThreadAsync(ObservableGroup<object, IMailListItem> group, MailItemViewModel item, MailCopy addedItem)
    {
        var threadViewModel = ThreadItemFactory(item.MailCopy.ThreadId);

        await ExecuteUIThread(() =>
        {
            threadViewModel.AddEmail(item);
            threadViewModel.AddEmail(new MailItemViewModel(addedItem));
        });

        var newGroupKey = GetGroupingKey(threadViewModel);

        await RemoveItemInternalAsync(group, item);
        await InsertItemInternalAsync(newGroupKey, threadViewModel);
    }

    public Task AddAsync(MailCopy addedItem)
        => RunSerializedAsync(() => AddInternalAsync(addedItem));

    private async Task AddInternalAsync(MailCopy addedItem)
    {
        // First check if this is an update to an existing item
        if (MailCopyIdHashSet.ContainsKey(addedItem.UniqueId))
        {
            // Find and update the existing item
            var existingItemContainer = GetMailItemContainer(addedItem.UniqueId);
            if (existingItemContainer?.ItemViewModel != null)
            {
                await UpdateExistingItemAsync(existingItemContainer, addedItem);
                return;
            }
        }

        // Check if this item should be threaded with an existing item
        if (!string.IsNullOrEmpty(addedItem.ThreadId))
        {
            var threadableItem = FindThreadableItem(addedItem.ThreadId);
            if (threadableItem != null)
            {
                // Find the group containing this item
                var targetGroup = FindGroupContainingItem(threadableItem);
                if (targetGroup != null)
                {
                    await HandleThreadingAsync(targetGroup, threadableItem, addedItem);
                    return;
                }
            }
        }

        // No threading needed, add as new item
        await AddNewItemAsync(addedItem);
    }

    private ObservableGroup<object, IMailListItem> FindGroupContainingItem(IMailListItem item)
    {
        // Try cache first
        if (_itemToGroupMap.TryGetValue(item, out var cachedGroup))
        {
            // Cache can become stale during concurrent list refreshes/moves.
            // Validate before returning so we don't mutate a detached group.
            if (_mailItemSource.Contains(cachedGroup) && cachedGroup.Contains(item))
            {
                return cachedGroup;
            }

            _itemToGroupMap.TryRemove(item, out _);
        }

        // Fallback to search if not in cache
        foreach (var group in _mailItemSource)
        {
            if (group.Contains(item))
            {
                _itemToGroupMap[item] = group;
                return group;
            }
        }
        return null;
    }

    private async Task AddNewItemAsync(MailCopy addedItem)
    {
        var newMailItem = new MailItemViewModel(addedItem);
        var groupKey = GetGroupingKey(newMailItem);
        await InsertItemInternalAsync(groupKey, newMailItem);
    }

    private async Task ReinsertUpdatedItemAsync(MailCopy updatedItem, bool isSelected, bool isBusy)
    {
        await RemoveInternalAsync(updatedItem);
        await AddInternalAsync(updatedItem);

        var updatedContainer = GetMailItemContainer(updatedItem.UniqueId);
        if (updatedContainer?.ItemViewModel == null)
        {
            return;
        }

        await ExecuteUIThread(() =>
        {
            updatedContainer.ItemViewModel.IsSelected = isSelected;
            updatedContainer.ItemViewModel.IsBusy = isBusy;
        });
    }

    private async Task UpdateExistingItemAsync(MailItemContainer itemContainer,
                                               MailCopy updatedItem,
                                               EntityUpdateSource mailUpdateSource = EntityUpdateSource.Server,
                                               MailCopyChangeFlags changeHint = MailCopyChangeFlags.None)
    {
        if (itemContainer?.ItemViewModel == null)
        {
            return;
        }

        var existingItem = itemContainer.ItemViewModel;
        var threadOwner = itemContainer.ThreadViewModel as IMailListItem ?? existingItem;
        var wasSelected = existingItem.IsSelected;
        MailCopyChangeFlags appliedChanges = MailCopyChangeFlags.None;

        await ExecuteUIThread(() =>
        {
            UpdateUniqueIdHashes(existingItem, false);
            UpdateThreadIdCache(threadOwner, false);

            itemContainer.ThreadViewModel?.SuspendChildPropertyNotifications();

            try
            {
                appliedChanges = existingItem.UpdateFrom(updatedItem, changeHint);
            }
            finally
            {
                itemContainer.ThreadViewModel?.ResumeChildPropertyNotifications();
            }

            existingItem.IsBusy = mailUpdateSource == EntityUpdateSource.ClientUpdated;

            UpdateUniqueIdHashes(existingItem, true);
            UpdateThreadIdCache(threadOwner, true);

            if (itemContainer.ThreadViewModel != null)
            {
                _uniqueIdToThreadMap[existingItem.MailCopy.UniqueId] = itemContainer.ThreadViewModel;
            }
            else
            {
                _uniqueIdToThreadMap.TryRemove(existingItem.MailCopy.UniqueId, out _);
            }
        });

        if (ShouldReinsertForChanges(appliedChanges))
        {
            await ReinsertUpdatedItemAsync(updatedItem, wasSelected, existingItem.IsBusy);
            return;
        }

        if (itemContainer.ThreadViewModel != null && appliedChanges != MailCopyChangeFlags.None)
        {
            await ExecuteUIThread(() =>
            {
                itemContainer.ThreadViewModel.NotifyMailItemUpdated(existingItem, appliedChanges);
            });
        }
    }

    /// <summary>
    /// Adds multiple emails to the collection.
    /// </summary>
    public Task AddRangeAsync(IEnumerable<MailItemViewModel> items, bool clearIdCache)
        => RunSerializedAsync(() => AddRangeInternalAsync(items, clearIdCache));

    private async Task AddRangeInternalAsync(IEnumerable<MailItemViewModel> items, bool clearIdCache)
    {
        if (clearIdCache)
        {
            MailCopyIdHashSet.Clear();
            _threadIdToItemsMap.Clear();
            _itemToGroupMap.Clear();
            _uniqueIdToMailItemMap.Clear();
            _uniqueIdToThreadMap.Clear();
        }

        var itemsList = items as List<MailItemViewModel> ?? items.ToList();
        if (itemsList.Count == 0) return;

        var itemsToAdd = new List<IMailListItem>(itemsList.Count);
        var processedItems = new HashSet<MailItemViewModel>(itemsList.Count);
        var itemsToUpdate = new List<(MailItemViewModel existing, MailCopy updated)>();
        var threadingOperations = new List<(ObservableGroup<object, IMailListItem> group, IMailListItem item, MailCopy addedItem)>();

        // Build a lookup for existing groups to avoid repeated searches
        var groupLookup = new Dictionary<IMailListItem, ObservableGroup<object, IMailListItem>>(_mailItemSource.Count * 10);
        foreach (var group in _mailItemSource)
        {
            foreach (var item in group)
            {
                groupLookup[item] = group;
            }
        }

        // Build thread lookup from the batch items
        var batchThreadLookup = new Dictionary<string, List<MailItemViewModel>>();
        foreach (var item in itemsList)
        {
            if (!string.IsNullOrEmpty(item.MailCopy.ThreadId))
            {
                if (!batchThreadLookup.TryGetValue(item.MailCopy.ThreadId, out var list))
                {
                    list = new List<MailItemViewModel>();
                    batchThreadLookup[item.MailCopy.ThreadId] = list;
                }
                list.Add(item);
            }
        }

        // Process items and handle threading
        foreach (var item in itemsList)
        {
            if (processedItems.Contains(item))
                continue;

            // Check if this is an update to an existing item
            if (MailCopyIdHashSet.ContainsKey(item.MailCopy.UniqueId))
            {
                var existingItemContainer = GetMailItemContainer(item.MailCopy.UniqueId);
                if (existingItemContainer?.ItemViewModel != null)
                {
                    itemsToUpdate.Add((existingItemContainer.ItemViewModel, item.MailCopy));
                    processedItems.Add(item);
                    continue;
                }
            }

            // Check if this item should be threaded
            if (!string.IsNullOrEmpty(item.MailCopy.ThreadId))
            {
                // Look for existing item with same ThreadId
                var existingThreadableItem = FindThreadableItem(item.MailCopy.ThreadId);

                if (existingThreadableItem != null)
                {
                    // Thread with existing item
                    if (groupLookup.TryGetValue(existingThreadableItem, out var targetGroup))
                    {
                        threadingOperations.Add((targetGroup, existingThreadableItem, item.MailCopy));
                        processedItems.Add(item);
                        continue;
                    }
                }

                // Look for other items in the current batch with same ThreadId
                if (batchThreadLookup.TryGetValue(item.MailCopy.ThreadId, out var threadableItems) && threadableItems.Count > 1)
                {
                    // Create a new thread with all matching items - defer UI operations
                    var threadViewModel = ThreadItemFactory(item.MailCopy.ThreadId);

                    // Add emails without UI thread for now
                    foreach (var threadItem in threadableItems)
                    {
                        threadViewModel.AddEmail(threadItem);
                    }

                    itemsToAdd.Add(threadViewModel);

                    // Mark all threaded items as processed
                    foreach (var threadItem in threadableItems)
                    {
                        processedItems.Add(threadItem);
                    }
                    continue;
                }
            }

            // No threading needed, add as single item
            itemsToAdd.Add(item);
            processedItems.Add(item);
        }

        // Execute all threading operations in a single UI thread call
        if (threadingOperations.Count > 0)
        {
            foreach (var (group, existingItem, addedItem) in threadingOperations)
            {
                await HandleThreadingAsync(group, existingItem, addedItem);
            }
        }

        // Execute all updates in a single UI thread call
        if (itemsToUpdate.Count > 0)
        {
            await ExecuteUIThread(() =>
            {
                foreach (var (existing, updated) in itemsToUpdate)
                {
                    UpdateUniqueIdHashes(existing, false);
                    existing.UpdateFrom(updated);
                    UpdateUniqueIdHashes(existing, true);
                }
            });
        }

        // Group items by their grouping key and add them in a single UI thread call
        if (itemsToAdd.Count > 0)
        {
            var groupedItems = await Task.Run(() => itemsToAdd
                .GroupBy(GetGroupingKey)
                .OrderBy(group => group.Key, listComparer)
                .Select(group => new
                {
                    Key = group.Key,
                    Items = group.OrderBy(item => (object)item, listComparer).ToList()
                })
                .ToList()).ConfigureAwait(false);

            await ExecuteUIThread(() =>
            {
                foreach (var groupedItem in groupedItems)
                {
                    var groupKey = groupedItem.Key;
                    var groupItems = groupedItem.Items;

                    // Update caches first
                    foreach (var item in groupItems)
                    {
                        UpdateUniqueIdHashes(item, true);
                        UpdateThreadIdCache(item, true);
                    }

                    foreach (var item in groupItems)
                    {
                        _mailItemSource.InsertItem(groupKey, listComparer, item, listComparer);

                        var targetGroup = _mailItemSource.FirstGroupByKeyOrDefault(groupKey);
                        if (targetGroup != null)
                        {
                            _itemToGroupMap[item] = targetGroup;
                        }
                    }
                }
            });
        }
    }

    public MailItemContainer GetMailItemContainer(Guid uniqueMailId)
    {
        // Fast path: use caches for O(1) lookup
        if (_uniqueIdToMailItemMap.TryGetValue(uniqueMailId, out var cachedMailItem))
        {
            if (_uniqueIdToThreadMap.TryGetValue(uniqueMailId, out var threadVM))
            {
                return new MailItemContainer(cachedMailItem, threadVM);
            }

            return new MailItemContainer(cachedMailItem);
        }

        // Fallback: scan all groups and populate caches
        for (int i = 0; i < _mailItemSource.Count; i++)
        {
            var group = _mailItemSource[i];

            for (int k = 0; k < group.Count; k++)
            {
                var item = group[k];

                if (item is MailItemViewModel singleMailItemViewModel && singleMailItemViewModel.MailCopy.UniqueId == uniqueMailId)
                {
                    _uniqueIdToMailItemMap[uniqueMailId] = singleMailItemViewModel;
                    return new MailItemContainer(singleMailItemViewModel);
                }
                else if (item is ThreadMailItemViewModel threadMailItemViewModel && threadMailItemViewModel.HasUniqueId(uniqueMailId))
                {
                    var singleItemViewModel = threadMailItemViewModel.ThreadEmails.FirstOrDefault(e => e.MailCopy.UniqueId == uniqueMailId);

                    if (singleItemViewModel != null)
                    {
                        _uniqueIdToMailItemMap[uniqueMailId] = singleItemViewModel;
                        _uniqueIdToThreadMap[uniqueMailId] = threadMailItemViewModel;
                    }

                    return new MailItemContainer(singleItemViewModel, threadMailItemViewModel);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Updates thumbnails for all mail items with the specified address.
    /// </summary>
    public Task UpdateThumbnailsForAddressAsync(string address)
    {
        if (CoreDispatcher == null) return Task.CompletedTask;

        return RunSerializedAsync(() => CoreDispatcher.ExecuteOnUIThread(() =>
        {
            foreach (var group in _mailItemSource)
            {
                foreach (var item in group)
                {
                    if (item is MailItemViewModel mailItemViewModel && mailItemViewModel.MailCopy.FromAddress?.Equals(address, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        mailItemViewModel.ThumbnailUpdatedEvent = !mailItemViewModel.ThumbnailUpdatedEvent;
                    }
                    else if (item is ThreadMailItemViewModel threadViewModel)
                    {
                        foreach (var threadMailItem in threadViewModel.ThreadEmails)
                        {
                            if (threadMailItem.MailCopy.FromAddress?.Equals(address, StringComparison.OrdinalIgnoreCase) == true)
                            {
                                threadMailItem.ThumbnailUpdatedEvent = !threadMailItem.ThumbnailUpdatedEvent;
                            }
                        }
                    }
                }
            }
        }));
    }

    /// <summary>
    /// Finds the item container that updated mail copy belongs to and updates it.
    /// </summary>
    /// <param name="updatedMailCopy">Updated mail copy.</param>
    /// <returns></returns>
    public Task UpdateMailCopy(MailCopy updatedMailCopy, EntityUpdateSource mailUpdateSource, MailCopyChangeFlags changedProperties = MailCopyChangeFlags.None)
        => RunSerializedAsync(() =>
        {
            var itemContainer = GetMailItemContainer(updatedMailCopy.UniqueId);

            if (itemContainer?.ItemViewModel == null)
            {
                return Task.CompletedTask;
            }

            return UpdateExistingItemAsync(itemContainer, updatedMailCopy, mailUpdateSource, changedProperties);
        });

    public Task UpdateMailStateAsync(MailStateChange updatedState, EntityUpdateSource mailUpdateSource)
        => RunSerializedAsync(() =>
        {
            if (updatedState == null)
                return Task.CompletedTask;

            var itemContainer = GetMailItemContainer(updatedState.UniqueId);

            if (itemContainer?.ItemViewModel == null)
            {
                return Task.CompletedTask;
            }

            return UpdateExistingMailStateAsync(itemContainer, updatedState, mailUpdateSource);
        });

    public Task UpdateMailStatesAsync(IEnumerable<MailStateChange> updatedStates, EntityUpdateSource mailUpdateSource)
        => RunSerializedAsync(() => UpdateMailStatesInternalAsync(updatedStates, mailUpdateSource));

    public Task UpdateMailCopiesAsync(IEnumerable<MailCopy> updatedMailCopies, EntityUpdateSource mailUpdateSource, MailCopyChangeFlags changedProperties = MailCopyChangeFlags.None)
        => RunSerializedAsync(() => UpdateMailCopiesInternalAsync(updatedMailCopies, mailUpdateSource, changedProperties));

    private async Task UpdateExistingMailStateAsync(MailItemContainer itemContainer, MailStateChange updatedState, EntityUpdateSource mailUpdateSource)
    {
        if (itemContainer?.ItemViewModel == null || updatedState == null)
            return;

        var existingItem = itemContainer.ItemViewModel;

        await ExecuteUIThread(() =>
        {
            var appliedChanges = existingItem.ApplyStateChanges(updatedState.IsRead, updatedState.IsFlagged);
            existingItem.IsBusy = mailUpdateSource == EntityUpdateSource.ClientUpdated;

            if (itemContainer.ThreadViewModel != null && appliedChanges != MailCopyChangeFlags.None)
            {
                itemContainer.ThreadViewModel.NotifyMailItemUpdated(existingItem, appliedChanges);
            }
        });
    }

    private async Task UpdateMailStatesInternalAsync(IEnumerable<MailStateChange> updatedStates, EntityUpdateSource mailUpdateSource)
    {
        var updates = updatedStates?
            .Where(x => x != null)
            .GroupBy(x => x.UniqueId)
            .Select(group =>
            {
                var updatedState = group.Last();
                return new
                {
                    UpdatedState = updatedState,
                    ItemContainer = GetMailItemContainer(updatedState.UniqueId)
                };
            })
            .Where(x => x.ItemContainer?.ItemViewModel != null)
            .ToList() ?? [];

        if (updates.Count == 0)
            return;

        await ExecuteUIThread(() =>
        {
            foreach (var update in updates)
            {
                var existingItem = update.ItemContainer.ItemViewModel;
                var appliedChanges = existingItem.ApplyStateChanges(update.UpdatedState.IsRead, update.UpdatedState.IsFlagged);
                existingItem.IsBusy = mailUpdateSource == EntityUpdateSource.ClientUpdated;

                if (update.ItemContainer.ThreadViewModel != null && appliedChanges != MailCopyChangeFlags.None)
                {
                    update.ItemContainer.ThreadViewModel.NotifyMailItemUpdated(existingItem, appliedChanges);
                }
            }
        });
    }

    private async Task UpdateMailCopiesInternalAsync(IEnumerable<MailCopy> updatedMailCopies, EntityUpdateSource mailUpdateSource, MailCopyChangeFlags changedProperties)
    {
        var updates = updatedMailCopies?
            .Where(x => x != null)
            .GroupBy(x => x.UniqueId)
            .Select(group =>
            {
                var updatedMail = group.First();
                return new
                {
                    UpdatedMail = updatedMail,
                    ItemContainer = GetMailItemContainer(updatedMail.UniqueId)
                };
            })
            .Where(x => x.ItemContainer?.ItemViewModel != null)
            .ToList() ?? [];

        if (updates.Count == 0)
            return;

        if (changedProperties == MailCopyChangeFlags.None || ShouldReinsertForChanges(changedProperties))
        {
            foreach (var update in updates)
            {
                await UpdateExistingItemAsync(update.ItemContainer, update.UpdatedMail, mailUpdateSource, changedProperties);
            }

            return;
        }

        await ExecuteUIThread(() =>
        {
            foreach (var update in updates)
            {
                var updatedMail = update.UpdatedMail;
                var itemContainer = update.ItemContainer;
                var existingItem = itemContainer.ItemViewModel;
                var appliedChanges = existingItem.UpdateFrom(updatedMail, changedProperties);
                existingItem.IsBusy = mailUpdateSource == EntityUpdateSource.ClientUpdated;

                if (itemContainer.ThreadViewModel != null && appliedChanges != MailCopyChangeFlags.None)
                {
                    itemContainer.ThreadViewModel.NotifyMailItemUpdated(existingItem, appliedChanges);
                }
            }
        });
    }

    public MailItemViewModel GetFirst() => AllItems.ElementAtOrDefault(0);

    public MailItemViewModel GetNextItem(MailCopy mailCopy)
    {
        try
        {
            var groupCount = _mailItemSource.Count;

            for (int i = 0; i < groupCount; i++)
            {
                var group = _mailItemSource[i];

                for (int k = 0; k < group.Count; k++)
                {
                    var item = group[k];

                    if (item is MailItemViewModel singleMailItemViewModel && singleMailItemViewModel.MailCopy.UniqueId == mailCopy.UniqueId)
                    {
                        if (k + 1 < group.Count)
                        {
                            return group[k + 1] as MailItemViewModel;
                        }
                        else if (i + 1 < groupCount)
                        {
                            return _mailItemSource[i + 1][0] as MailItemViewModel;
                        }
                        else
                        {
                            return null;
                        }
                    }
                    else if (item is ThreadMailItemViewModel threadMailItemViewModel && threadMailItemViewModel.HasUniqueId(mailCopy.UniqueId))
                    {
                        var singleItemViewModel = threadMailItemViewModel.ThreadEmails.FirstOrDefault(e => e.MailCopy.UniqueId == mailCopy.UniqueId);

                        if (singleItemViewModel == null) return null;

                        var singleItemIndex = threadMailItemViewModel.ThreadEmails.ToList().IndexOf(singleItemViewModel);

                        if (singleItemIndex + 1 < threadMailItemViewModel.ThreadEmails.Count)
                        {
                            return threadMailItemViewModel.ThreadEmails[singleItemIndex + 1];
                        }
                        else if (i + 1 < groupCount)
                        {
                            return _mailItemSource[i + 1][0] as MailItemViewModel;
                        }
                        else
                        {
                            return null;
                        }
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to find the next item to select.");
        }

        return null;
    }

    public Task RemoveAsync(MailCopy removeItem)
        => RunSerializedAsync(() => RemoveInternalAsync(removeItem));

    public Task RemoveRangeAsync(IEnumerable<MailCopy> removeItems)
        => RunSerializedAsync(() => RemoveRangeInternalAsync(removeItems));

    private async Task RemoveInternalAsync(MailCopy removeItem)
        => await RemoveInternalAsync(removeItem, notifySelectionChanges: true);

    private async Task RemoveRangeInternalAsync(IEnumerable<MailCopy> removeItems)
    {
        var distinctItems = removeItems?
            .Where(x => x != null)
            .GroupBy(x => x.UniqueId)
            .Select(group => group.First())
            .ToList() ?? [];

        if (distinctItems.Count == 0)
            return;

        foreach (var removeItem in distinctItems)
        {
            await RemoveInternalAsync(removeItem, notifySelectionChanges: false);
        }

        await NotifySelectionChangesAsync();
    }

    private async Task RemoveInternalAsync(MailCopy removeItem, bool notifySelectionChanges)
    {
        var itemContainer = GetMailItemContainer(removeItem.UniqueId);

        // This item doesn't exist in the list.
        if (itemContainer?.ItemViewModel == null) return;

        if (itemContainer.ThreadViewModel != null)
        {
            // Item is inside a thread - use cached lookups instead of scanning all groups.
            var threadMailItemViewModel = itemContainer.ThreadViewModel;
            var group = FindGroupContainingItem(threadMailItemViewModel);
            if (group == null) return;

            var removalItem = itemContainer.ItemViewModel;

            // Update ThreadId cache before modifying the thread
            UpdateThreadIdCache(threadMailItemViewModel, false);

            await ExecuteUIThread(() => { threadMailItemViewModel.RemoveEmail(removalItem); });

            // Always clean up the removed item's hashes (fixes leak when thread converts to single)
            UpdateUniqueIdHashes(removalItem, false);

            // Update ThreadId cache after modifying the thread
            if (threadMailItemViewModel.EmailCount > 0)
            {
                UpdateThreadIdCache(threadMailItemViewModel, true);
            }

            if (threadMailItemViewModel.EmailCount == 1)
            {
                // Convert to single item.
                var singleViewModel = threadMailItemViewModel.ThreadEmails.First();
                var groupKey = GetGroupingKey(singleViewModel);

                await RemoveItemInternalAsync(group, threadMailItemViewModel);
                await InsertItemInternalAsync(groupKey, singleViewModel);

                // If thread->single conversion is being done, we should ignore it for non-draft items.
                // eg. Deleting a reply message from draft folder. Single non-draft item should not be re-added.
                if (PruneSingleNonDraftItems && !singleViewModel.IsDraft)
                {
                    var newGroup = _mailItemSource.FirstGroupByKeyOrDefault(groupKey);
                    if (newGroup != null)
                    {
                        await RemoveItemInternalAsync(newGroup, singleViewModel);
                    }
                }
            }
            else if (threadMailItemViewModel.EmailCount == 0)
            {
                await RemoveItemInternalAsync(group, threadMailItemViewModel);
            }
        }
        else
        {
            // Standalone item.
            IMailListItem mailItem = itemContainer.ItemViewModel;
            var group = FindGroupContainingItem(mailItem);

            if (group != null)
            {
                await RemoveItemInternalAsync(group, mailItem);
            }
        }

        if (notifySelectionChanges)
        {
            await NotifySelectionChangesAsync();
        }
    }

    private IEnumerable<IMailListItem> AllItemsIncludingThreads
    {
        get
        {
            foreach (var group in _mailItemSource)
            {
                foreach (var item in group)
                {
                    if (item is ThreadMailItemViewModel threadMailItemViewModel)
                    {
                        foreach (var child in threadMailItemViewModel.ThreadEmails)
                        {
                            yield return child;
                        }
                    }
                    yield return item;
                }
            }
        }
    }

    private IEnumerable<MailItemViewModel> AllItems
    {
        get
        {
            foreach (var group in _mailItemSource)
            {
                foreach (var item in group)
                {
                    if (item is ThreadMailItemViewModel threadMail)
                    {
                        foreach (var singleItem in threadMail.ThreadEmails)
                        {
                            yield return singleItem;
                        }
                    }
                    else if (item is MailItemViewModel mailItemViewModel)
                        yield return mailItemViewModel;
                }
            }
        }
    }

    public IEnumerable<MailItemViewModel> SelectedItems => AllItems.Where(a => a.IsSelected);
    public int SelectedItemsCount => AllItems.Count(a => a.IsSelected);
    public int AllItemsCount => AllItems.Count();
    public bool IsAllItemsSelected => AllItems.Any() && AllItems.All(a => a.IsSelected);
    public bool HasSingleItemSelected => SelectedItemsCount == 1;

    public async Task ExecuteSelectionBatchAsync(Action action, bool notifySelectionChanged = true)
    {
        try
        {
            _selectionNotificationSuppressionCount++;
            await ExecuteUIThread(action);
        }
        catch (Exception)
        {
        }
        finally
        {
            _selectionNotificationSuppressionCount = Math.Max(0, _selectionNotificationSuppressionCount - 1);

            if (_selectionNotificationSuppressionCount == 0)
            {
                var shouldNotify = notifySelectionChanged || _selectionNotificationPending;
                _selectionNotificationPending = false;

                if (shouldNotify)
                {
                    await NotifySelectionChangesAsync();
                }
            }
        }
    }

    public Task ExecuteWithoutRaiseSelectionChangedAsync(Action<IMailListItem> action, bool includeThreads)
        => ExecuteSelectionBatchAsync(() =>
        {
            if (includeThreads)
            {
                foreach (var item in AllItemsIncludingThreads)
                {
                    action(item);
                }
            }
            else
            {
                foreach (var item in AllItems)
                {
                    action(item);
                }
            }
        });

    public Task ToggleSelectAllAsync()
    {
        if (IsAllItemsSelected)
        {
            return UnselectAllAsync();
        }
        else
        {
            return SelectAllAsync();
        }
    }

    /// <summary>
    /// Gets the index of an item in the flat Items collection.
    /// Note: WinoMailCollection doesn't have a flat Items collection like GroupedEmailCollection.
    /// This returns -1 as it's not applicable to the grouped structure.
    /// </summary>
    public int IndexOf(object item)
    {
        // WinoMailCollection uses grouped structure, so we need to search through groups
        int currentIndex = 0;

        foreach (var group in _mailItemSource)
        {
            foreach (var groupItem in group)
            {
                if (ReferenceEquals(groupItem, item))
                {
                    return currentIndex;
                }
                currentIndex++;
            }
        }

        return -1;
    }

    public Task SelectAllAsync() => ExecuteWithoutRaiseSelectionChangedAsync(a => a.IsSelected = true, true);
    public Task UnselectAllAsync(IMailListItem exceptItem = null) => ExecuteWithoutRaiseSelectionChangedAsync(a => { if (a != exceptItem) a.IsSelected = false; }, true);
    public Task CollapseAllThreadsAsync() => ExecuteWithoutRaiseSelectionChangedAsync(a => { if (a is ThreadMailItemViewModel thread) thread.IsThreadExpanded = false; }, true);

    private Task ExecuteUIThread(Action action) => CoreDispatcher?.ExecuteOnUIThread(action);

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

    public void Receive(SelectedItemsChangedMessage message)
    {
        if (_selectionNotificationSuppressionCount > 0)
        {
            _selectionNotificationPending = true;
            return;
        }

        _ = NotifySelectionChangesAsync();
    }

    private async Task NotifySelectionChangesAsync()
    {
        await ExecuteUIThread(() =>
        {
            OnPropertyChanged(nameof(IsAllItemsSelected));
            OnPropertyChanged(nameof(SelectedItemsCount));
            OnPropertyChanged(nameof(HasSingleItemSelected));

            ItemSelectionChanged?.Invoke(this, null);
        });
    }

}
