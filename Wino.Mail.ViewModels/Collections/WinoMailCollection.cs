using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Collections;
using Serilog;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels.Collections;

public class WinoMailCollection
{
    // We cache each mail copy id for faster access on updates.
    // If the item provider here for update or removal doesn't exist here
    // we can ignore the operation.

    public HashSet<Guid> MailCopyIdHashSet = [];

    public event EventHandler<MailItemViewModel> MailItemRemoved;

    private ListItemComparer listComparer = new();

    private readonly ObservableGroupedCollection<object, IMailListItem> _mailItemSource = new ObservableGroupedCollection<object, IMailListItem>();

    public ReadOnlyObservableGroupedCollection<object, IMailListItem> MailItems { get; }

    /// <summary>
    /// Property that defines how the item sorting should be done in the collection.
    /// </summary>
    public SortingOptionType SortingType { get; set; }

    /// <summary>
    /// Automatically deletes single mail items after the delete operation or thread->single transition.
    /// This is useful when reply draft is discarded in the thread. Only enabled for Draft folder for now.
    /// </summary>
    public bool PruneSingleNonDraftItems { get; set; }

    public int Count => _mailItemSource.Count;

    public IDispatcher CoreDispatcher { get; set; }

    public WinoMailCollection()
    {
        MailItems = new ReadOnlyObservableGroupedCollection<object, IMailListItem>(_mailItemSource);
    }

    public void Clear() => _mailItemSource.Clear();

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

    private void InsertItemInternal(object groupKey, IMailListItem mailItem)
    {
        UpdateUniqueIdHashes(mailItem, true);
        _mailItemSource.InsertItem(groupKey, listComparer, mailItem, listComparer);
    }

    private void RemoveItemInternal(ObservableGroup<object, IMailListItem> group, IMailListItem mailItem)
    {
        UpdateUniqueIdHashes(mailItem, false);

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

        group.Remove(mailItem);

        if (group.Count == 0)
        {
            _mailItemSource.RemoveGroup(group.Key);
        }
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

        await ExecuteUIThread(() => 
        { 
            var newMailItem = new MailItemViewModel(addedItem);
            threadViewModel.AddEmail(newMailItem);
        });

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
        await ExecuteUIThread(() =>
        {
            RemoveItemInternal(currentGroup, threadViewModel);
            InsertItemInternal(newGroupKey, threadViewModel);
        });
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

        await ExecuteUIThread(() =>
        {
            RemoveItemInternal(group, item);
            InsertItemInternal(newGroupKey, threadViewModel);
        });
    }

    public async Task AddAsync(MailCopy addedItem)
    {
        foreach (var group in _mailItemSource)
        {
            foreach (var item in group)
            {
                // Compare ThreadIds - if they match and both have ThreadIds, thread them together
                bool shouldThread = !string.IsNullOrEmpty(addedItem.ThreadId) && 
                                  item is MailItemViewModel mailItem && 
                                  !string.IsNullOrEmpty(mailItem.MailCopy.ThreadId) &&
                                  string.Equals(addedItem.ThreadId, mailItem.MailCopy.ThreadId, StringComparison.OrdinalIgnoreCase);

                if (!shouldThread && item is ThreadMailItemViewModel threadViewModel)
                {
                    // Check if any email in the thread has matching ThreadId
                    shouldThread = !string.IsNullOrEmpty(addedItem.ThreadId) &&
                                 threadViewModel.ThreadEmails.Any(e => 
                                     !string.IsNullOrEmpty(e.MailCopy.ThreadId) &&
                                     string.Equals(addedItem.ThreadId, e.MailCopy.ThreadId, StringComparison.OrdinalIgnoreCase));
                }

                if (shouldThread)
                {
                    await HandleThreadingAsync(group, item, addedItem);
                    return;
                }
                else if (item is MailItemViewModel itemViewModel && itemViewModel.MailCopy.UniqueId == addedItem.UniqueId)
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
        var newMailItem = new MailItemViewModel(addedItem);
        var groupKey = GetGroupingKey(newMailItem);
        await ExecuteUIThread(() => { InsertItemInternal(groupKey, newMailItem); });
    }

    private async Task UpdateExistingItemAsync(MailItemViewModel existingItem, MailCopy updatedItem)
    {
        UpdateUniqueIdHashes(existingItem, false);
        UpdateUniqueIdHashes(new MailItemViewModel(updatedItem), true);

        await ExecuteUIThread(() => { existingItem.MailCopy = updatedItem; });
    }

    public void AddRange(IEnumerable<IMailListItem> items, bool clearIdCache)
    {
        if (clearIdCache)
        {
            MailCopyIdHashSet.Clear();
        }

        var groupedByName = items
                            .GroupBy(a => GetGroupingKey(a))
                            .Select(a => new ObservableGroup<object, IMailListItem>(a.Key, a));

        foreach (var group in groupedByName)
        {
            // Store all mail copy ids for faster access.
            foreach (var item in group)
            {
                UpdateUniqueIdHashes(item, true);
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
                    else if (item is ThreadMailItemViewModel threadViewModel)
                    {
                        foreach (var threadMailItem in threadViewModel.ThreadEmails)
                        {
                            if (threadMailItem.MailCopy.FromAddress.Equals(address, StringComparison.OrdinalIgnoreCase))
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

            UpdateUniqueIdHashes(new MailItemViewModel(updatedMailCopy), true);

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

                    await ExecuteUIThread(() => { threadMailItemViewModel.RemoveEmail(removalItem); });

                    if (threadMailItemViewModel.EmailCount == 1)
                    {
                        // Convert to single item.

                        var singleViewModel = threadMailItemViewModel.ThreadEmails.First();
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
                    else if (threadMailItemViewModel.EmailCount == 0)
                    {
                        await ExecuteUIThread(() => { RemoveItemInternal(group, threadMailItemViewModel); });
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
                    await ExecuteUIThread(() => { RemoveItemInternal(group, item); });

                    shouldExit = true;

                    break;
                }
            }
        }
    }

    private async Task ExecuteUIThread(Action action) => await CoreDispatcher?.ExecuteOnUIThread(action);
}
