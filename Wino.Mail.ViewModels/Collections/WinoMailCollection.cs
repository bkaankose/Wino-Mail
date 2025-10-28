using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using MoreLinq.Extensions;
using Serilog;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Mails;

namespace Wino.Mail.ViewModels.Collections;

public class WinoMailCollection : ObservableRecipient, IRecipient<SelectedItemsChangedMessage>
{
    // We cache each mail copy id for faster access on updates.
    // If the item provider here for update or removal doesn't exist here
    // we can ignore the operation.

    public HashSet<Guid> MailCopyIdHashSet = [];

    // Cache ThreadIds to quickly find items that should be threaded together
    private readonly Dictionary<string, List<IMailListItem>> _threadIdToItemsMap = new();

    public event EventHandler<MailItemViewModel> MailItemRemoved;
    public event EventHandler ItemSelectionChanged;

    private ListItemComparer listComparer = new();

    private readonly ObservableGroupedCollection<object, IMailListItem> _mailItemSource = new ObservableGroupedCollection<object, IMailListItem>();

    public ReadOnlyObservableGroupedCollection<object, IMailListItem> MailItems { get; }

    /// <summary>
    /// Property that defines how the item sorting should be done in the collection.
    /// </summary>
    public SortingOptionType SortingType { get; set; }

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

        Messenger.Register<SelectedItemsChangedMessage>(this);
    }

    public void Cleanup()
    {
        Messenger.Unregister<SelectedItemsChangedMessage>(this);
    }

    public async Task ClearAsync()
    {
        await ExecuteUIThread(() =>
        {
            _mailItemSource.Clear();
            MailCopyIdHashSet.Clear();
            _threadIdToItemsMap.Clear();
        });
    }

    private object GetGroupingKey(IMailListItem mailItem)
    {
        if (SortingType == SortingOptionType.ReceiveDate)
            return mailItem.CreationDate.ToLocalTime().Date;
        else
            return mailItem.FromName;
    }

    private void UpdateUniqueIdHashes(IMailHashContainer itemContainer, bool isAdd)
    {
        foreach (var item in itemContainer.GetContainingIds())
        {
            if (isAdd)
            {
                MailCopyIdHashSet.Add(item);
            }
            else
            {
                MailCopyIdHashSet.Remove(item);
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
                if (!_threadIdToItemsMap.ContainsKey(threadId))
                {
                    _threadIdToItemsMap[threadId] = new List<IMailListItem>();
                }
                _threadIdToItemsMap[threadId].Add(item);
            }
            else
            {
                if (_threadIdToItemsMap.ContainsKey(threadId))
                {
                    _threadIdToItemsMap[threadId].Remove(item);
                    if (_threadIdToItemsMap[threadId].Count == 0)
                    {
                        _threadIdToItemsMap.Remove(threadId);
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

    private IMailListItem FindThreadableItem(string threadId)
    {
        if (string.IsNullOrEmpty(threadId) || !_threadIdToItemsMap.ContainsKey(threadId))
        {
            return null;
        }

        return _threadIdToItemsMap[threadId].FirstOrDefault();
    }

    private async Task InsertItemInternalAsync(object groupKey, IMailListItem mailItem)
    {
        UpdateUniqueIdHashes(mailItem, true);
        UpdateThreadIdCache(mailItem, true);
        await ExecuteUIThread(() =>
        {
            _mailItemSource.InsertItem(groupKey, listComparer, mailItem, listComparer);
        });
    }

    private async Task RemoveItemInternalAsync(ObservableGroup<object, IMailListItem> group, IMailListItem mailItem)
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
        }

        await ExecuteUIThread(() =>
        {
            group.Remove(mailItem);

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

        await ExecuteUIThread(() =>
        {
            var newMailItem = new MailItemViewModel(addedItem);
            threadViewModel.AddEmail(newMailItem);
        });

        // Update ThreadId cache after modifying the thread
        UpdateThreadIdCache(threadViewModel, true);

        var newGroupKey = GetGroupingKey(threadViewModel);

        if (!existingGroupKey.Equals(newGroupKey))
        {
            await MoveThreadToNewGroupAsync(group, threadViewModel, newGroupKey);
        }
        else
        {
            await ExecuteUIThread(() => { threadViewModel.NotifyPropertyChanges(); });
        }

        UpdateUniqueIdHashes(new MailItemViewModel(addedItem), true);
    }

    private async Task HandleNewThreadAsync(ObservableGroup<object, IMailListItem> group, MailItemViewModel item, MailCopy addedItem)
    {
        if (item.MailCopy.UniqueId == addedItem.UniqueId)
        {
            await UpdateExistingItemAsync(item, addedItem);
        }
        else
        {
            await CreateNewThreadAsync(group, item, addedItem);
        }
    }

    private async Task MoveThreadToNewGroupAsync(ObservableGroup<object, IMailListItem> currentGroup, ThreadMailItemViewModel threadViewModel, object newGroupKey)
    {
        await RemoveItemInternalAsync(currentGroup, threadViewModel);
        await InsertItemInternalAsync(newGroupKey, threadViewModel);
    }

    private async Task CreateNewThreadAsync(ObservableGroup<object, IMailListItem> group, MailItemViewModel item, MailCopy addedItem)
    {
        var threadViewModel = new ThreadMailItemViewModel(item.MailCopy.ThreadId);

        await ExecuteUIThread(() =>
        {
            threadViewModel.AddEmail(item);
            threadViewModel.AddEmail(new MailItemViewModel(addedItem));
        });

        var newGroupKey = GetGroupingKey(threadViewModel);

        await RemoveItemInternalAsync(group, item);
        await InsertItemInternalAsync(newGroupKey, threadViewModel);
    }

    public async Task AddAsync(MailCopy addedItem)
    {
        // First check if this is an update to an existing item
        if (MailCopyIdHashSet.Contains(addedItem.UniqueId))
        {
            // Find and update the existing item
            var existingItemContainer = GetMailItemContainer(addedItem.UniqueId);
            if (existingItemContainer?.ItemViewModel != null)
            {
                await UpdateExistingItemAsync(existingItemContainer.ItemViewModel, addedItem);
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
        foreach (var group in _mailItemSource)
        {
            if (group.Contains(item))
            {
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

    private async Task UpdateExistingItemAsync(MailItemViewModel existingItem, MailCopy updatedItem)
    {
        UpdateUniqueIdHashes(existingItem, false);
        UpdateUniqueIdHashes(new MailItemViewModel(updatedItem), true);

        await ExecuteUIThread(() => { existingItem.NotifyPropertyChanges(); });
    }

    /// <summary>
    /// Adds multiple emails to the collection.
    /// </summary>
    public async Task AddRangeAsync(IEnumerable<MailItemViewModel> items, bool clearIdCache)
    {
        if (clearIdCache)
        {
            MailCopyIdHashSet.Clear();
            _threadIdToItemsMap.Clear();
        }

        var itemsList = items.ToList();
        var itemsToAdd = new List<IMailListItem>();
        var processedItems = new HashSet<MailItemViewModel>();

        // Process items and handle threading
        foreach (var item in itemsList)
        {
            if (processedItems.Contains(item))
                continue;

            // Check if this is an update to an existing item
            if (MailCopyIdHashSet.Contains(item.MailCopy.UniqueId))
            {
                var existingItemContainer = GetMailItemContainer(item.MailCopy.UniqueId);
                if (existingItemContainer?.ItemViewModel != null)
                {
                    await UpdateExistingItemAsync(existingItemContainer.ItemViewModel, item.MailCopy);
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
                    var targetGroup = FindGroupContainingItem(existingThreadableItem);
                    if (targetGroup != null)
                    {
                        await HandleThreadingAsync(targetGroup, existingThreadableItem, item.MailCopy);
                        processedItems.Add(item);
                        continue;
                    }
                }

                // Look for other items in the current batch with same ThreadId
                var threadableItems = itemsList
                    .Where(i => !processedItems.Contains(i) &&
                               !string.IsNullOrEmpty(i.MailCopy.ThreadId) &&
                               i.MailCopy.ThreadId == item.MailCopy.ThreadId)
                    .ToList();

                if (threadableItems.Count > 1)
                {
                    // Create a new thread with all matching items
                    var threadViewModel = new ThreadMailItemViewModel(item.MailCopy.ThreadId);

                    await ExecuteUIThread(() =>
                    {
                        foreach (var threadItem in threadableItems)
                        {
                            threadViewModel.AddEmail(threadItem);
                        }
                    });

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

        // Group items by their grouping key and add them
        var groupedItems = itemsToAdd
            .GroupBy(GetGroupingKey)
            .Select(g => new ObservableGroup<object, IMailListItem>(g.Key, g));

        await ExecuteUIThread(() =>
        {
            foreach (var group in groupedItems)
            {
                foreach (var item in group)
                {
                    UpdateUniqueIdHashes(item, true);
                    UpdateThreadIdCache(item, true);
                }

                var existingGroup = _mailItemSource.FirstGroupByKeyOrDefault(group.Key);

                if (existingGroup == null)
                {
                    _mailItemSource.AddGroup(group.Key, group);
                }
                else
                {
                    foreach (var item in group)
                    {
                        existingGroup.Add(item);
                    }
                }
            }
        });
    }

    public MailItemContainer GetMailItemContainer(Guid uniqueMailId)
    {
        var groupCount = _mailItemSource.Count;

        for (int i = 0; i < groupCount; i++)
        {
            var group = _mailItemSource[i];

            for (int k = 0; k < group.Count; k++)
            {
                var item = group[k];

                if (item is MailItemViewModel singleMailItemViewModel && singleMailItemViewModel.MailCopy.UniqueId == uniqueMailId)
                    return new MailItemContainer(singleMailItemViewModel);
                else if (item is ThreadMailItemViewModel threadMailItemViewModel && threadMailItemViewModel.HasUniqueId(uniqueMailId))
                {
                    var singleItemViewModel = threadMailItemViewModel.ThreadEmails.FirstOrDefault(e => e.MailCopy.UniqueId == uniqueMailId);

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

        return CoreDispatcher.ExecuteOnUIThread(() =>
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
        });
    }

    /// <summary>
    /// Fins the item container that updated mail copy belongs to and updates it.
    /// </summary>
    /// <param name="updatedMailCopy">Updated mail copy.</param>
    /// <returns></returns>
    public Task UpdateMailCopy(MailCopy updatedMailCopy)
    {
        // This item doesn't exist in the list.
        if (!MailCopyIdHashSet.Contains(updatedMailCopy.UniqueId)) return Task.CompletedTask;

        return ExecuteUIThread(() =>
        {
            var itemContainer = GetMailItemContainer(updatedMailCopy.UniqueId);

            if (itemContainer == null) return;

            if (itemContainer.ItemViewModel != null)
            {
                UpdateUniqueIdHashes(itemContainer.ItemViewModel, false);
            }

            if (itemContainer.ItemViewModel != null)
            {
                itemContainer.ItemViewModel.NotifyPropertyChanges();
            }

            UpdateUniqueIdHashes(new MailItemViewModel(updatedMailCopy), true);

            // Call thread notifications if possible.
            itemContainer.ThreadViewModel?.NotifyPropertyChanges();
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

    public async Task RemoveAsync(MailCopy removeItem)
    {
        // This item doesn't exist in the list.
        if (!MailCopyIdHashSet.Contains(removeItem.UniqueId)) return;

        // Check all items for whether this item should be threaded with them.
        bool shouldExit = false;

        var groupCount = _mailItemSource.Count;

        for (int i = 0; i < groupCount; i++)
        {
            if (shouldExit) break;

            var group = _mailItemSource[i];

            for (int k = 0; k < group.Count; k++)
            {
                var item = group[k];

                if (item is ThreadMailItemViewModel threadMailItemViewModel && threadMailItemViewModel.HasUniqueId(removeItem.UniqueId))
                {
                    var removalItem = threadMailItemViewModel.ThreadEmails.FirstOrDefault(e => e.MailCopy.UniqueId == removeItem.UniqueId);

                    if (removalItem == null) return;

                    // Threads' Id is equal to the last item they hold.
                    // We can't do Id check here because that'd remove the whole thread.

                    /* Remove item from the thread.
                     * If thread had 1 item inside:
                     * -> Remove the thread and insert item as single item.
                     * If thread had 0 item inside:
                     * -> Remove the thread.
                     */

                    var oldGroupKey = GetGroupingKey(threadMailItemViewModel);

                    // Update ThreadId cache before modifying the thread
                    UpdateThreadIdCache(threadMailItemViewModel, false);

                    await ExecuteUIThread(() => { threadMailItemViewModel.RemoveEmail(removalItem); });

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
                            // This item should not be here anymore.
                            // It's basically a reply mail in Draft folder.
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
                    else
                    {
                        // Item inside the thread is removed - update hash
                        UpdateUniqueIdHashes(removalItem, false);
                    }

                    shouldExit = true;
                    break;
                }
                else if (item is MailItemViewModel mailItemViewModel && mailItemViewModel.MailCopy.UniqueId == removeItem.UniqueId)
                {
                    await RemoveItemInternalAsync(group, item);

                    shouldExit = true;

                    break;
                }
            }
        }

        await NotifySelectionChangesAsync();
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

    public async Task ExecuteWithoutRaiseSelectionChangedAsync(Action<IMailListItem> action, bool includeThreads)
    {
        try
        {
            // Do not listen to individual selection changes while we are doing bulk selection.
            Messenger.Unregister<SelectedItemsChangedMessage>(this);

            await ExecuteUIThread(() =>
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
        }
        catch (Exception)
        {
        }
        finally
        {
            Messenger.Register<SelectedItemsChangedMessage>(this);
            Messenger.Send(new SelectedItemsChangedMessage());

            await NotifySelectionChangesAsync();
        }
    }

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
    public Task UnselectAllAsync() => ExecuteWithoutRaiseSelectionChangedAsync(a => a.IsSelected = false, true);
    public Task CollapseAllThreadsAsync() => ExecuteWithoutRaiseSelectionChangedAsync(a => { if (a is ThreadMailItemViewModel thread) thread.IsThreadExpanded = false; }, true);

    private async Task ExecuteUIThread(Action action) => await CoreDispatcher?.ExecuteOnUIThread(action);

    public void Receive(SelectedItemsChangedMessage message) => _ = NotifySelectionChangesAsync();

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
