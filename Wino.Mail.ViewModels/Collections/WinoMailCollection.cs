using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Collections;
using Serilog;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Comparers;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels.Collections;

public class WinoMailCollection
{
    // We cache each mail copy id for faster access on updates.
    // If the item provider here for update or removal doesn't exist here
    // we can ignore the operation.

    public HashSet<Guid> MailCopyIdHashSet = [];

    public event EventHandler<IMailItem> MailItemRemoved;

    private ListItemComparer listComparer = new ListItemComparer();

    private readonly ObservableGroupedCollection<object, IMailItem> _mailItemSource = new ObservableGroupedCollection<object, IMailItem>();

    public ReadOnlyObservableGroupedCollection<object, IMailItem> MailItems { get; }

    /// <summary>
    /// Property that defines how the item sorting should be done in the collection.
    /// </summary>
    public SortingOptionType SortingType { get; set; }

    /// <summary>
    /// Threading strategy that will help thread items according to the account type.
    /// </summary>
    public IThreadingStrategyProvider ThreadingStrategyProvider { get; set; }

    /// <summary>
    /// Automatically deletes single mail items after the delete operation or thread->single transition.
    /// This is useful when reply draft is discarded in the thread. Only enabled for Draft folder for now.
    /// </summary>
    public bool PruneSingleNonDraftItems { get; set; }

    public int Count => _mailItemSource.Count;

    public IDispatcher CoreDispatcher { get; set; }

    private readonly ThreadingManager _threadingManager;

    public WinoMailCollection(IThreadingStrategyProvider threadingStrategyProvider)
    {
        _threadingManager = new ThreadingManager(threadingStrategyProvider);
        MailItems = new ReadOnlyObservableGroupedCollection<object, IMailItem>(_mailItemSource);
    }

    public void Clear() => _mailItemSource.Clear();

    private object GetGroupingKey(IMailItem mailItem)
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

    private void InsertItemInternal(object groupKey, IMailItem mailItem)
    {
        UpdateUniqueIdHashes(mailItem, true);

        if (mailItem is MailCopy mailCopy)
        {
            _mailItemSource.InsertItem(groupKey, listComparer, new MailItemViewModel(mailCopy), listComparer.GetItemComparer());
        }
        else if (mailItem is ThreadMailItem threadMailItem)
        {
            _mailItemSource.InsertItem(groupKey, listComparer, new ThreadMailItemViewModel(threadMailItem), listComparer.GetItemComparer());
        }
        else
        {
            _mailItemSource.InsertItem(groupKey, listComparer, mailItem, listComparer.GetItemComparer());
        }
    }

    private void RemoveItemInternal(ObservableGroup<object, IMailItem> group, IMailItem mailItem)
    {
        UpdateUniqueIdHashes(mailItem, false);

        MailItemRemoved?.Invoke(this, mailItem);

        group.Remove(mailItem);

        if (group.Count == 0)
        {
            _mailItemSource.RemoveGroup(group.Key);
        }
    }

    private async Task HandleThreadingAsync(ObservableGroup<object, IMailItem> group, IMailItem item, MailCopy addedItem)
    {
        if (item is ThreadMailItemViewModel threadViewModel)
        {
            await HandleExistingThreadAsync(group, threadViewModel, addedItem);
        }
        else
        {
            await HandleNewThreadAsync(group, item, addedItem);
        }
    }

    private async Task HandleExistingThreadAsync(ObservableGroup<object, IMailItem> group, ThreadMailItemViewModel threadViewModel, MailCopy addedItem)
    {
        var existingGroupKey = GetGroupingKey(threadViewModel);

        await ExecuteUIThread(() => { threadViewModel.AddMailItemViewModel(addedItem); });

        var newGroupKey = GetGroupingKey(threadViewModel);

        if (!existingGroupKey.Equals(newGroupKey))
        {
            await MoveThreadToNewGroupAsync(group, threadViewModel, newGroupKey);
        }
        else
        {
            await ExecuteUIThread(() => { threadViewModel.NotifyPropertyChanges(); });
        }

        UpdateUniqueIdHashes(addedItem, true);
    }

    private async Task HandleNewThreadAsync(ObservableGroup<object, IMailItem> group, IMailItem item, MailCopy addedItem)
    {
        if (item.Id == addedItem.Id)
        {
            await UpdateExistingItemAsync(item, addedItem);
        }
        else
        {
            await CreateNewThreadAsync(group, item, addedItem);
        }
    }

    private async Task MoveThreadToNewGroupAsync(ObservableGroup<object, IMailItem> currentGroup, ThreadMailItemViewModel threadViewModel, object newGroupKey)
    {
        var mailThreadItems = threadViewModel.GetThreadMailItem();

        await ExecuteUIThread(() =>
        {
            RemoveItemInternal(currentGroup, threadViewModel);
            InsertItemInternal(newGroupKey, new ThreadMailItemViewModel(mailThreadItems));
        });
    }

    private async Task CreateNewThreadAsync(ObservableGroup<object, IMailItem> group, IMailItem item, MailCopy addedItem)
    {
        var threadMailItem = _threadingManager.CreateNewThread(item, addedItem);
        var newGroupKey = GetGroupingKey(threadMailItem);

        await ExecuteUIThread(() =>
        {
            RemoveItemInternal(group, item);
            InsertItemInternal(newGroupKey, threadMailItem);
        });
    }

    public async Task AddAsync(MailCopy addedItem)
    {
        foreach (var group in _mailItemSource)
        {
            foreach (var item in group)
            {
                if (_threadingManager.ShouldThread(addedItem, item))
                {
                    await HandleThreadingAsync(group, item, addedItem);
                    return;
                }
                else if (item.Id == addedItem.Id && item is MailItemViewModel itemViewModel)
                {
                    await UpdateExistingItemAsync(itemViewModel, addedItem);
                    return;
                }
            }
        }

        await AddNewItemAsync(addedItem);
    }

    private async Task AddNewItemAsync(MailCopy addedItem)
    {
        var groupKey = GetGroupingKey(addedItem);
        await ExecuteUIThread(() => { InsertItemInternal(groupKey, addedItem); });
    }

    private async Task UpdateExistingItemAsync(IMailItem existingItem, MailCopy updatedItem)
    {
        if (existingItem is MailItemViewModel itemViewModel)
        {
            UpdateUniqueIdHashes(itemViewModel, false);
            UpdateUniqueIdHashes(updatedItem, true);

            await ExecuteUIThread(() => { itemViewModel.MailCopy = updatedItem; });
        }
    }

    public void AddRange(IEnumerable<IMailItem> items, bool clearIdCache)
    {
        if (clearIdCache)
        {
            MailCopyIdHashSet.Clear();
        }

        var groupedByName = items
                            .GroupBy(a => GetGroupingKey(a))
                            .Select(a => new ObservableGroup<object, IMailItem>(a.Key, a));

        foreach (var group in groupedByName)
        {
            // Store all mail copy ids for faster access.
            foreach (var item in group)
            {
                if (item is MailItemViewModel mailCopyItem && !MailCopyIdHashSet.Contains(item.UniqueId))
                {
                    MailCopyIdHashSet.Add(item.UniqueId);
                }
                else if (item is ThreadMailItemViewModel threadMailItem)
                {
                    foreach (var mailItem in threadMailItem.ThreadItems)
                    {
                        if (!MailCopyIdHashSet.Contains(mailItem.UniqueId))
                        {
                            MailCopyIdHashSet.Add(mailItem.UniqueId);
                        }
                    }
                }
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

                if (item is MailItemViewModel singleMailItemViewModel && singleMailItemViewModel.UniqueId == uniqueMailId)
                    return new MailItemContainer(singleMailItemViewModel);
                else if (item is ThreadMailItemViewModel threadMailItemViewModel && threadMailItemViewModel.HasUniqueId(uniqueMailId))
                {
                    var singleItemViewModel = threadMailItemViewModel.GetItemById(uniqueMailId) as MailItemViewModel;

                    return new MailItemContainer(singleItemViewModel, threadMailItemViewModel);
                }
            }
        }

        return null;
    }

    public void UpdateThumbnails(string address)
    {
        if (CoreDispatcher == null) return;

        CoreDispatcher.ExecuteOnUIThread(() =>
        {
            foreach (var group in _mailItemSource)
            {
                foreach (var item in group)
                {
                    if (item is MailItemViewModel mailItemViewModel && mailItemViewModel.MailCopy.FromAddress.Equals(address, StringComparison.OrdinalIgnoreCase))
                    {
                        mailItemViewModel.ThumbnailUpdatedEvent = !mailItemViewModel.ThumbnailUpdatedEvent;
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
    public async Task UpdateMailCopy(MailCopy updatedMailCopy)
    {
        // This item doesn't exist in the list.
        if (!MailCopyIdHashSet.Contains(updatedMailCopy.UniqueId))

        {
            return;
        }

        await ExecuteUIThread(() =>
        {
            var itemContainer = GetMailItemContainer(updatedMailCopy.UniqueId);

            if (itemContainer == null) return;

            if (itemContainer.ItemViewModel != null)
            {
                UpdateUniqueIdHashes(itemContainer.ItemViewModel, false);
            }

            if (itemContainer.ItemViewModel != null)
            {
                itemContainer.ItemViewModel.MailCopy = updatedMailCopy;
            }

            UpdateUniqueIdHashes(updatedMailCopy, true);

            // Call thread notifications if possible.
            itemContainer.ThreadViewModel?.NotifyPropertyChanges();
        });
    }

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

                    if (item is MailItemViewModel singleMailItemViewModel && singleMailItemViewModel.UniqueId == mailCopy.UniqueId)
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
                        var singleItemViewModel = threadMailItemViewModel.GetItemById(mailCopy.UniqueId) as MailItemViewModel;

                        if (singleItemViewModel == null) return null;

                        var singleItemIndex = threadMailItemViewModel.ThreadItems.IndexOf(singleItemViewModel);

                        if (singleItemIndex + 1 < threadMailItemViewModel.ThreadItems.Count)
                        {
                            return threadMailItemViewModel.ThreadItems[singleItemIndex + 1] as MailItemViewModel;
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
                    var removalItem = threadMailItemViewModel.GetItemById(removeItem.UniqueId);

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

                    await ExecuteUIThread(() => { threadMailItemViewModel.RemoveCopyItem(removalItem); });

                    if (threadMailItemViewModel.ThreadItems.Count == 1)
                    {
                        // Convert to single item.

                        var singleViewModel = threadMailItemViewModel.GetSingleItemViewModel();
                        var groupKey = GetGroupingKey(singleViewModel);

                        await ExecuteUIThread(() =>
                        {
                            RemoveItemInternal(group, threadMailItemViewModel);
                            InsertItemInternal(groupKey, singleViewModel);
                        });

                        // If thread->single conversion is being done, we should ignore it for non-draft items.
                        // eg. Deleting a reply message from draft folder. Single non-draft item should not be re-added.

                        if (PruneSingleNonDraftItems && !singleViewModel.IsDraft)
                        {
                            // This item should not be here anymore.
                            // It's basically a reply mail in Draft folder.
                            var newGroup = _mailItemSource.FirstGroupByKeyOrDefault(groupKey);

                            if (newGroup != null)
                            {
                                await ExecuteUIThread(() => { RemoveItemInternal(newGroup, singleViewModel); });
                            }
                        }
                    }
                    else if (threadMailItemViewModel.ThreadItems.Count == 0)
                    {
                        await ExecuteUIThread(() => { RemoveItemInternal(group, threadMailItemViewModel); });
                    }
                    else
                    {
                        // Item inside the thread is removed.
                        await ExecuteUIThread(() => { threadMailItemViewModel.ThreadItems.Remove(removalItem); });

                        UpdateUniqueIdHashes(removalItem, false);
                    }

                    shouldExit = true;
                    break;
                }
                else if (item.UniqueId == removeItem.UniqueId)
                {
                    await ExecuteUIThread(() => { RemoveItemInternal(group, item); });

                    shouldExit = true;

                    break;
                }
            }
        }
    }

    private async Task ExecuteUIThread(Action action) => await CoreDispatcher?.ExecuteOnUIThread(action);
}
