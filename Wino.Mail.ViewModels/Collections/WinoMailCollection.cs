using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Serilog;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.MailItem;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.Client.Mails;
using Wino.Messaging.UI;

namespace Wino.Mail.ViewModels.Collections;

public class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void AddRange(IEnumerable<T> items)
    {
        var itemList = items?.Where(static item => item != null).ToList() ?? [];
        if (itemList.Count == 0)
        {
            return;
        }

        foreach (var item in itemList)
        {
            Items.Add(item);
        }

        RaiseReset();
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();

        foreach (var item in items?.Where(static item => item != null) ?? [])
        {
            Items.Add(item);
        }

        RaiseReset();
    }

    public void RemoveRange(IEnumerable<T> items)
    {
        var itemList = items?.Where(static item => item != null).ToList() ?? [];

        if (itemList.Count == 0)
        {
            return;
        }

        if (itemList.Count == 1)
        {
            Remove(itemList[0]);
            return;
        }

        var removedAny = false;

        foreach (var item in itemList)
        {
            removedAny |= Items.Remove(item);
        }

        if (removedAny)
        {
            RaiseReset();
        }
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public sealed class WinoMailGroup : BulkObservableCollection<IMailListItem>
{
    public WinoMailGroup(object key, IEnumerable<IMailListItem> items)
    {
        Key = key;
        AddRange(items);
    }

    public object Key { get; }

    public new WinoMailGroup Items => this;
}

public class WinoMailCollection : ObservableRecipient, IRecipient<SelectedItemsChangedMessage>
{
    private readonly List<IMailListItem> _topLevelItems = [];
    private readonly ListItemComparer _listComparer = new();
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly ConcurrentDictionary<string, List<IMailListItem>> _threadIdToItemsMap = new();
    private readonly ConcurrentDictionary<Guid, MailItemViewModel> _uniqueIdToMailItemMap = new();
    private readonly ConcurrentDictionary<Guid, ThreadMailItemViewModel> _uniqueIdToThreadMap = new();
    private readonly ConcurrentDictionary<IMailListItem, WinoMailGroup> _itemToGroupMap = new();

    private int _selectionNotificationSuppressionCount;
    private bool _selectionNotificationPending;
    private SortingOptionType _sortingType;

    public ConcurrentDictionary<Guid, bool> MailCopyIdHashSet = [];

    public event EventHandler<MailItemViewModel> MailItemRemoved;
    public event EventHandler ItemSelectionChanged;

    public Func<MailCopy, MailItemViewModel> MailItemFactory { get; set; } = static mailCopy => new MailItemViewModel(mailCopy);
    public Func<string, ThreadMailItemViewModel> ThreadItemFactory { get; set; } = static threadId => new ThreadMailItemViewModel(threadId, true);
    public bool IsThreadingEnabled { get; set; } = true;
    public bool AreGroupHeadersEnabled { get; set; } = true;
    public bool PruneSingleNonDraftItems { get; set; }
    public IDispatcher CoreDispatcher { get; set; }

    public BulkObservableCollection<WinoMailGroup> MailItems { get; } = [];

    private sealed class AddMailResult
    {
        public static AddMailResult NoChange { get; } = new();
        public static AddMailResult FullRebuild { get; } = new() { RequiresFullGroupRebuild = true };

        public bool RequiresFullGroupRebuild { get; private init; }
        public IReadOnlyList<IMailListItem> TouchedItems { get; private init; } = [];

        public static AddMailResult For(params IMailListItem[] touchedItems)
            => new() { TouchedItems = touchedItems.Where(static item => item != null).ToList() };
    }

    private sealed class RemoveMailResult
    {
        public static RemoveMailResult NoChange { get; } = new();

        public IReadOnlyList<IMailListItem> TouchedItems { get; private init; } = [];

        public static RemoveMailResult For(params IMailListItem[] touchedItems)
            => new() { TouchedItems = touchedItems.Where(static item => item != null).ToList() };
    }

    private sealed class UpdateMailResult
    {
        public static UpdateMailResult NoChange { get; } = new();

        public IReadOnlyList<IMailListItem> TouchedItems { get; private init; } = [];

        public static UpdateMailResult For(params IMailListItem[] touchedItems)
            => new() { TouchedItems = touchedItems.Where(static item => item != null).ToList() };
    }

    public SortingOptionType SortingType
    {
        get => _sortingType;
        set
        {
            if (_sortingType == value)
            {
                return;
            }

            _sortingType = value;
            _listComparer.SortByName = value == SortingOptionType.Sender;
            _ = ExecuteUIThread(() =>
            {
                SortTopLevelItems();
                RebuildGroups();
            });
        }
    }

    public EmailGroupingType GroupingType
    {
        get => SortingType == SortingOptionType.ReceiveDate ? EmailGroupingType.ByDate : EmailGroupingType.ByFromName;
        set => SortingType = value == EmailGroupingType.ByDate ? SortingOptionType.ReceiveDate : SortingOptionType.Sender;
    }

    public int Count => _topLevelItems.Count;
    public bool IsAllSelected => AllItemsCount == SelectedItemsCount;
    public int AllItemsCount => MailCopyIdHashSet.Count;

    public WinoMailCollection()
    {
        SortingType = SortingOptionType.ReceiveDate;
        Messenger.Register(this);
    }

    public void Cleanup()
    {
        Messenger.Unregister<SelectedItemsChangedMessage>(this);
        DetachThreadHandlers();
    }

    public Task ClearAsync()
        => RunSerializedAsync(async () =>
        {
            await ExecuteUIThread(() =>
            {
                DetachThreadHandlers();
                _topLevelItems.Clear();
                MailItems.Clear();
                ClearIndexes();
            });

            await NotifySelectionChangesAsync();
        });

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsThreadId(string threadId)
        => !string.IsNullOrEmpty(threadId) && _threadIdToItemsMap.ContainsKey(threadId);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ContainsMailUniqueId(Guid uniqueId) => MailCopyIdHashSet.ContainsKey(uniqueId);

    public MailItemViewModel Find(Guid uniqueId)
    {
        if (_uniqueIdToMailItemMap.TryGetValue(uniqueId, out var cachedMailItem))
        {
            return cachedMailItem;
        }

        RebuildMailLookupIndexes();
        return _uniqueIdToMailItemMap.TryGetValue(uniqueId, out cachedMailItem) ? cachedMailItem : null;
    }

    public ThreadMailItemViewModel GetThreadByMailUniqueId(Guid uniqueId)
    {
        if (_uniqueIdToThreadMap.TryGetValue(uniqueId, out var threadViewModel))
        {
            return threadViewModel;
        }

        RebuildMailLookupIndexes();
        return _uniqueIdToThreadMap.TryGetValue(uniqueId, out threadViewModel) ? threadViewModel : null;
    }

    public List<ThreadMailItemViewModel> GetThreadItems()
        => _topLevelItems.OfType<ThreadMailItemViewModel>().ToList();

    public void UpdateAccountNicknamePosition(AccountNicknamePosition position)
    {
        foreach (var mailItem in _uniqueIdToMailItemMap.Values.Distinct())
        {
            mailItem.AccountNicknamePosition = position;
        }

        foreach (var threadItem in _topLevelItems.OfType<ThreadMailItemViewModel>())
        {
            threadItem.AccountNicknamePosition = position;
        }
    }

    public Task AddAsync(MailCopy addedItem)
        => RunSerializedAsync(async () =>
        {
            if (addedItem == null)
            {
                return;
            }

            await ExecuteUIThread(() =>
            {
                var addResult = AddMailCore(MailItemFactory(addedItem));
                SortTopLevelItems();
                RebuildMailLookupIndexes();

                if (addResult.RequiresFullGroupRebuild)
                {
                    RebuildGroups();
                }
                else
                {
                    RefreshGroupsIncrementally(addResult.TouchedItems);
                }
            });
        });

    public Task AddRangeAsync(IEnumerable<MailItemViewModel> items, bool clearIdCache)
        => RunSerializedAsync(async () =>
        {
            var mailItems = items?.Where(static item => item != null).ToList() ?? [];
            if (mailItems.Count == 0 && !clearIdCache)
            {
                return;
            }

            await ExecuteUIThread(() =>
            {
                if (clearIdCache)
                {
                    DetachThreadHandlers();
                    _topLevelItems.Clear();
                    ClearIndexes();
                }

                var touchedItems = new List<IMailListItem>();
                var requiresFullRebuild = clearIdCache;

                foreach (var item in mailItems)
                {
                    var addResult = AddMailCore(item);
                    requiresFullRebuild |= addResult.RequiresFullGroupRebuild;
                    touchedItems.AddRange(addResult.TouchedItems);
                    RebuildMailLookupIndexes();
                }

                SortTopLevelItems();
                RebuildMailLookupIndexes();

                if (requiresFullRebuild)
                {
                    RebuildGroups();
                }
                else
                {
                    RefreshGroupsIncrementally(touchedItems);
                }
            });
        });

    public Task UpdateThumbnailsForAddressAsync(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return Task.CompletedTask;
        }

        return RunSerializedAsync(() => ExecuteUIThread(() =>
        {
            foreach (var mailItem in EnumerateLeafItems())
            {
                if (mailItem.MailCopy.FromAddress?.Equals(address, StringComparison.OrdinalIgnoreCase) == true)
                {
                    mailItem.ThumbnailUpdatedEvent = !mailItem.ThumbnailUpdatedEvent;
                }
            }
        }));
    }

    public Task UpdateMailCopy(MailCopy updatedMailCopy, EntityUpdateSource mailUpdateSource, MailCopyChangeFlags changedProperties = MailCopyChangeFlags.None)
        => RunSerializedAsync(async () =>
        {
            if (updatedMailCopy == null)
            {
                return;
            }

            await ExecuteUIThread(() =>
            {
                var updateResult = UpdateMailCopyCore(updatedMailCopy, mailUpdateSource, changedProperties);
                SortTopLevelItems();
                RebuildMailLookupIndexes();
                RefreshGroupsIncrementally(updateResult.TouchedItems);
            });
        });

    public Task UpdateMailStateAsync(MailStateChange updatedState, EntityUpdateSource mailUpdateSource)
        => RunSerializedAsync(async () =>
        {
            if (updatedState == null)
            {
                return;
            }

            await ExecuteUIThread(() => UpdateMailStateCore(updatedState, mailUpdateSource));
        });

    public Task UpdateMailStatesAsync(IEnumerable<MailStateChange> updatedStates, EntityUpdateSource mailUpdateSource)
        => RunSerializedAsync(async () =>
        {
            var states = updatedStates?
                .Where(static state => state != null)
                .GroupBy(static state => state.UniqueId)
                .Select(static group => group.Last())
                .ToList() ?? [];

            if (states.Count == 0)
            {
                return;
            }

            await ExecuteUIThread(() =>
            {
                foreach (var state in states)
                {
                    UpdateMailStateCore(state, mailUpdateSource);
                }
            });
        });

    public Task UpdateMailCopiesAsync(IEnumerable<MailCopy> updatedMailCopies, EntityUpdateSource mailUpdateSource, MailCopyChangeFlags changedProperties = MailCopyChangeFlags.None)
        => RunSerializedAsync(async () =>
        {
            var copies = updatedMailCopies?
                .Where(static copy => copy != null)
                .GroupBy(static copy => copy.UniqueId)
                .Select(static group => group.Last())
                .ToList() ?? [];

            if (copies.Count == 0)
            {
                return;
            }

            await ExecuteUIThread(() =>
            {
                var touchedItems = new List<IMailListItem>();

                foreach (var copy in copies)
                {
                    touchedItems.AddRange(UpdateMailCopyCore(copy, mailUpdateSource, changedProperties).TouchedItems);
                    RebuildMailLookupIndexes();
                }

                SortTopLevelItems();
                RebuildMailLookupIndexes();
                RefreshGroupsIncrementally(touchedItems);
            });
        });

    public MailItemViewModel GetFirst() => EnumerateLeafItems().FirstOrDefault();

    public MailItemViewModel GetNextItem(MailCopy mailCopy)
    {
        try
        {
            var leaves = EnumerateLeafItems().ToList();
            var index = leaves.FindIndex(item => item.MailCopy.UniqueId == mailCopy.UniqueId);
            return index >= 0 && index + 1 < leaves.Count ? leaves[index + 1] : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to find the next item to select.");
            return null;
        }
    }

    public Task RemoveAsync(MailCopy removeItem)
        => RunSerializedAsync(async () =>
        {
            if (removeItem == null)
            {
                return;
            }

            await ExecuteUIThread(() =>
            {
                var removeResult = RemoveMailCore(removeItem.UniqueId);
                SortTopLevelItems();
                RebuildMailLookupIndexes();
                RefreshGroupsIncrementally(removeResult.TouchedItems);
            });

            await NotifySelectionChangesAsync();
        });

    public Task RemoveRangeAsync(IEnumerable<MailCopy> removeItems)
        => RunSerializedAsync(async () =>
        {
            var uniqueIds = removeItems?
                .Where(static item => item != null)
                .Select(static item => item.UniqueId)
                .Distinct()
                .ToList() ?? [];

            if (uniqueIds.Count == 0)
            {
                return;
            }

            await ExecuteUIThread(() =>
            {
                var touchedItems = new List<IMailListItem>();

                foreach (var uniqueId in uniqueIds)
                {
                    touchedItems.AddRange(RemoveMailCore(uniqueId).TouchedItems);
                    RebuildMailLookupIndexes();
                }

                SortTopLevelItems();
                RebuildMailLookupIndexes();
                RefreshGroupsIncrementally(touchedItems);
            });

            await NotifySelectionChangesAsync();
        });

    public MailItemContainer GetMailItemContainer(Guid uniqueMailId)
    {
        if (_uniqueIdToMailItemMap.TryGetValue(uniqueMailId, out var mailItem))
        {
            return _uniqueIdToThreadMap.TryGetValue(uniqueMailId, out var thread)
                ? new MailItemContainer(mailItem, thread)
                : new MailItemContainer(mailItem);
        }

        RebuildMailLookupIndexes();

        if (!_uniqueIdToMailItemMap.TryGetValue(uniqueMailId, out mailItem))
        {
            return null;
        }

        return _uniqueIdToThreadMap.TryGetValue(uniqueMailId, out var rebuiltThread)
            ? new MailItemContainer(mailItem, rebuiltThread)
            : new MailItemContainer(mailItem);
    }

    public IEnumerable<MailItemViewModel> SelectedItems => EnumerateLeafItems().Where(static item => item.IsSelected);

    public int SelectedItemsCount
    {
        get
        {
            var count = 0;
            foreach (var item in EnumerateLeafItems())
            {
                if (item.IsSelected)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public bool IsAllItemsSelected
    {
        get
        {
            var total = MailCopyIdHashSet.Count;
            return total > 0 && SelectedItemsCount == total;
        }
    }

    public bool HasSingleItemSelected => SelectedItemsCount == 1;

    public async Task ExecuteSelectionBatchAsync(Action action, bool notifySelectionChanged = true)
    {
        try
        {
            _selectionNotificationSuppressionCount++;
            await ExecuteUIThread(action);
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
            var items = includeThreads
                ? EnumerateTopLevelAndLeafItems()
                : EnumerateLeafItems().Cast<IMailListItem>();

            foreach (var item in items)
            {
                action(item);
            }
        });

    public Task ToggleSelectAllAsync() => IsAllItemsSelected ? UnselectAllAsync() : SelectAllAsync();

    public int IndexOf(object item) => _topLevelItems.FindIndex(topLevelItem => ReferenceEquals(topLevelItem, item));

    public Task SelectTopLevelItemAsync(IMailListItem item, bool isMultiSelectionEnabled)
        => ExecuteSelectionBatchAsync(() =>
        {
            if (item == null)
            {
                return;
            }

            if (isMultiSelectionEnabled)
            {
                switch (item)
                {
                    case MailItemViewModel mail:
                        mail.IsSelected = !mail.IsSelected;
                        break;
                    case ThreadMailItemViewModel thread:
                        var shouldSelectThread = !thread.IsSelected || !thread.ThreadEmails.All(static child => child.IsSelected);
                        SetThreadAndChildrenSelection(thread, shouldSelectThread);

                        if (shouldSelectThread)
                        {
                            thread.IsThreadExpanded = true;
                        }

                        break;
                }

                return;
            }

            var wasSelected = item switch
            {
                MailItemViewModel mail => mail.IsSelected,
                ThreadMailItemViewModel thread => thread.IsSelected || thread.ThreadEmails.Any(static child => child.IsSelected),
                _ => false
            };

            ClearSelectionState();

            switch (item)
            {
                case MailItemViewModel mail:
                    CollapseAllThreadsExcept(null);
                    mail.IsSelected = !wasSelected;
                    break;
                case ThreadMailItemViewModel thread:
                    var isExpanding = !thread.IsThreadExpanded;
                    CollapseAllThreadsExcept(thread);
                    thread.IsThreadExpanded = isExpanding;
                    if (wasSelected)
                    {
                        break;
                    }

                    thread.IsSelected = true;

                    var selectedChild = thread.GetDefaultSelectedThreadEmail();
                    if (selectedChild != null)
                    {
                        selectedChild.IsSelected = true;
                    }

                    break;
            }
        });

    public Task SelectThreadMailAsync(ThreadMailItemViewModel thread, MailItemViewModel mail, bool isMultiSelectionEnabled)
        => ExecuteSelectionBatchAsync(() =>
        {
            if (thread == null || mail == null)
            {
                return;
            }

            if (isMultiSelectionEnabled)
            {
                mail.IsSelected = !mail.IsSelected;
                SyncThreadSelectionFromChildren(thread);
                return;
            }

            var wasSelected = mail.IsSelected;

            ClearSelectionState();
            CollapseAllThreadsExcept(thread);
            thread.IsThreadExpanded = true;

            if (wasSelected)
            {
                return;
            }

            thread.IsSelected = true;
            mail.IsSelected = true;
        });

    public Task SelectMailAsync(Guid uniqueId)
        => ExecuteSelectionBatchAsync(() =>
        {
            if (uniqueId == Guid.Empty)
            {
                return;
            }

            var selectedMail = Find(uniqueId);
            if (selectedMail == null)
            {
                return;
            }

            ClearSelectionState();

            var parentThread = GetThreadByMailUniqueId(uniqueId);
            if (parentThread != null)
            {
                CollapseAllThreadsExcept(parentThread);
                parentThread.IsThreadExpanded = true;
                parentThread.IsSelected = true;
            }
            else
            {
                CollapseAllThreadsExcept(null);
            }

            selectedMail.IsSelected = true;
        });

    public Task ToggleThreadExpansionAsync(ThreadMailItemViewModel thread)
        => ExecuteSelectionBatchAsync(() =>
        {
            if (thread == null)
            {
                return;
            }

            var isExpanding = !thread.IsThreadExpanded;
            CollapseAllThreadsExcept(thread);
            thread.IsThreadExpanded = isExpanding;
        }, notifySelectionChanged: false);

    public Task SelectRangeAsync(IReadOnlyList<IMailListItem> scope, IMailListItem anchor, IMailListItem target, bool preserveExistingSelection)
        => ExecuteSelectionBatchAsync(() =>
        {
            if (scope == null || anchor == null || target == null)
            {
                return;
            }

            var anchorIndex = IndexOfReference(scope, anchor);
            var targetIndex = IndexOfReference(scope, target);

            if (anchorIndex < 0 || targetIndex < 0)
            {
                return;
            }

            if (!preserveExistingSelection)
            {
                ClearSelectionState();
            }

            var startIndex = Math.Min(anchorIndex, targetIndex);
            var endIndex = Math.Max(anchorIndex, targetIndex);

            for (var i = startIndex; i <= endIndex; i++)
            {
                SelectListItemForRange(scope[i]);
            }

            SyncAllThreadSelectionsFromChildren();
        });

    public Task KeepNewestSelectionOnlyAsync()
        => ExecuteSelectionBatchAsync(() =>
        {
            var selectedMail = EnumerateLeafItems()
                .Where(static item => item.IsSelected)
                .OrderByDescending(static item => item.CreationDate)
                .FirstOrDefault();

            ClearSelectionState();

            if (selectedMail == null)
            {
                return;
            }

            selectedMail.IsSelected = true;
            var parentThread = GetThreadByMailUniqueId(selectedMail.UniqueId);
            if (parentThread != null)
            {
                parentThread.IsSelected = true;
                parentThread.IsThreadExpanded = true;
            }
        });

    public Task SelectAllAsync()
        => ExecuteSelectionBatchAsync(() =>
        {
            foreach (var item in _topLevelItems)
            {
                switch (item)
                {
                    case MailItemViewModel mail:
                        mail.IsSelected = true;
                        break;
                    case ThreadMailItemViewModel thread:
                        thread.IsSelected = true;
                        foreach (var child in thread.ThreadEmails)
                        {
                            child.IsSelected = true;
                        }

                        break;
                }
            }
        });

    public Task UnselectAllAsync(IMailListItem exceptItem = null)
        => ExecuteSelectionBatchAsync(() =>
        {
            foreach (var item in _topLevelItems)
            {
                if (!ReferenceEquals(item, exceptItem))
                {
                    item.IsSelected = false;
                }

                if (item is ThreadMailItemViewModel thread)
                {
                    foreach (var child in thread.ThreadEmails)
                    {
                        if (!ReferenceEquals(child, exceptItem))
                        {
                            child.IsSelected = false;
                        }
                    }

                    thread.IsSelected = thread.ThreadEmails.Any(static child => child.IsSelected);
                }
            }
        });

    public Task CollapseAllThreadsAsync()
        => ExecuteSelectionBatchAsync(() =>
        {
            foreach (var thread in _topLevelItems.OfType<ThreadMailItemViewModel>())
            {
                thread.IsThreadExpanded = false;
            }
        });

    private void ClearSelectionState()
    {
        foreach (var item in _topLevelItems)
        {
            switch (item)
            {
                case MailItemViewModel mail:
                    mail.IsSelected = false;
                    break;
                case ThreadMailItemViewModel thread:
                    SetThreadAndChildrenSelection(thread, false);
                    break;
            }
        }
    }

    private void CollapseAllThreadsExcept(ThreadMailItemViewModel exceptThread)
    {
        foreach (var thread in _topLevelItems.OfType<ThreadMailItemViewModel>())
        {
            if (!ReferenceEquals(thread, exceptThread))
            {
                thread.IsThreadExpanded = false;
            }
        }
    }

    private static void SetThreadAndChildrenSelection(ThreadMailItemViewModel thread, bool isSelected)
    {
        thread.IsSelected = isSelected;

        foreach (var child in thread.ThreadEmails)
        {
            child.IsSelected = isSelected;
        }
    }

    private static void SyncThreadSelectionFromChildren(ThreadMailItemViewModel thread)
    {
        thread.IsSelected = thread.ThreadEmails.Any(static child => child.IsSelected);

        if (thread.IsSelected)
        {
            thread.IsThreadExpanded = true;
        }
    }

    private void SyncAllThreadSelectionsFromChildren()
    {
        foreach (var thread in _topLevelItems.OfType<ThreadMailItemViewModel>())
        {
            SyncThreadSelectionFromChildren(thread);
        }
    }

    private static int IndexOfReference(IReadOnlyList<IMailListItem> items, IMailListItem target)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], target))
            {
                return i;
            }
        }

        return -1;
    }

    private static void SelectListItemForRange(IMailListItem item)
    {
        switch (item)
        {
            case MailItemViewModel mail:
                mail.IsSelected = true;
                break;
            case ThreadMailItemViewModel thread:
                SetThreadAndChildrenSelection(thread, true);
                thread.IsThreadExpanded = true;
                break;
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

    private AddMailResult AddMailCore(MailItemViewModel item)
    {
        if (MailCopyIdHashSet.ContainsKey(item.MailCopy.UniqueId))
        {
            UpdateMailCopyCore(item.MailCopy, EntityUpdateSource.Server);
            return AddMailResult.FullRebuild;
        }

        var threadId = item.MailCopy.ThreadId;
        if (IsThreadingEnabled && !string.IsNullOrEmpty(threadId))
        {
            var threadableItem = FindThreadableItem(threadId, item.MailCopy.UniqueId);
            if (threadableItem != null)
            {
                return AddToThread(threadableItem, item);
            }
        }

        _topLevelItems.Add(item);
        return AddMailResult.For(item);
    }

    private AddMailResult AddToThread(IMailListItem threadableItem, MailItemViewModel newItem)
    {
        if (threadableItem is ThreadMailItemViewModel thread)
        {
            thread.AddEmail(newItem);
            thread.IsSelected = thread.IsSelected || thread.ThreadEmails.Any(static child => child.IsSelected);
            return AddMailResult.For(thread);
        }

        if (threadableItem is not MailItemViewModel existingMail)
        {
            return AddMailResult.NoChange;
        }

        var threadViewModel = ThreadItemFactory(existingMail.MailCopy.ThreadId);
        var existingIndex = _topLevelItems.IndexOf(existingMail);

        threadViewModel.AddEmail(existingMail);
        threadViewModel.AddEmail(newItem);
        threadViewModel.IsSelected = existingMail.IsSelected || newItem.IsSelected;

        if (existingIndex >= 0)
        {
            _topLevelItems[existingIndex] = threadViewModel;
        }
        else
        {
            _topLevelItems.Add(threadViewModel);
        }

        return AddMailResult.For(existingMail, threadViewModel);
    }

    private RemoveMailResult RemoveMailCore(Guid uniqueId)
    {
        var container = GetMailItemContainer(uniqueId);
        if (container?.ItemViewModel == null)
        {
            return RemoveMailResult.NoChange;
        }

        var removedMail = container.ItemViewModel;
        RemoveMailResult result;

        if (container.ThreadViewModel != null)
        {
            result = RemoveThreadMail(container.ThreadViewModel, removedMail);
        }
        else
        {
            _topLevelItems.Remove(removedMail);
            result = RemoveMailResult.For(removedMail);
        }

        MailItemRemoved?.Invoke(this, removedMail);
        return result;
    }

    private RemoveMailResult RemoveThreadMail(ThreadMailItemViewModel thread, MailItemViewModel mail)
    {
        var threadIndex = _topLevelItems.IndexOf(thread);
        thread.RemoveEmail(mail);

        if (thread.EmailCount == 0)
        {
            thread.UnregisterThreadEmailPropertyChangedHandlers();
            _topLevelItems.Remove(thread);
            return RemoveMailResult.For(thread, mail);
        }

        if (thread.EmailCount != 1)
        {
            thread.IsSelected = thread.ThreadEmails.Any(static child => child.IsSelected);
            return RemoveMailResult.For(thread, mail);
        }

        var singleViewModel = thread.ThreadEmails[0];
        thread.RemoveEmail(singleViewModel);
        thread.UnregisterThreadEmailPropertyChangedHandlers();

        if (PruneSingleNonDraftItems && !singleViewModel.IsDraft)
        {
            _topLevelItems.Remove(thread);
            MailItemRemoved?.Invoke(this, singleViewModel);
            return RemoveMailResult.For(thread, mail, singleViewModel);
        }

        singleViewModel.IsDisplayedInThread = false;

        if (threadIndex >= 0)
        {
            _topLevelItems[threadIndex] = singleViewModel;
        }
        else
        {
            _topLevelItems.Remove(thread);
            _topLevelItems.Add(singleViewModel);
        }

        return RemoveMailResult.For(thread, mail, singleViewModel);
    }

    private UpdateMailResult UpdateMailCopyCore(MailCopy updatedMailCopy, EntityUpdateSource mailUpdateSource, MailCopyChangeFlags changedProperties = MailCopyChangeFlags.None)
    {
        var itemContainer = GetMailItemContainer(updatedMailCopy.UniqueId);
        if (itemContainer?.ItemViewModel == null)
        {
            return UpdateMailResult.NoChange;
        }

        var existingItem = itemContainer.ItemViewModel;
        var wasSelected = existingItem.IsSelected;
        var wasBusy = existingItem.IsBusy;
        var shouldReinsertFromHint = ShouldReinsertBeforeApplyingUpdate(existingItem, updatedMailCopy, changedProperties);

        if (shouldReinsertFromHint)
        {
            var removeResult = RemoveMailCore(existingItem.MailCopy.UniqueId);
            RebuildMailLookupIndexes();
            var replacement = MailItemFactory(updatedMailCopy);
            replacement.IsSelected = wasSelected;
            replacement.IsBusy = mailUpdateSource == EntityUpdateSource.ClientUpdated || wasBusy;
            var addResult = AddMailCore(replacement);
            return UpdateMailResult.For(removeResult.TouchedItems.Concat(addResult.TouchedItems).ToArray());
        }

        MailCopyChangeFlags appliedChanges = MailCopyChangeFlags.None;

        itemContainer.ThreadViewModel?.SuspendChildPropertyNotifications();
        try
        {
            appliedChanges = existingItem.UpdateFrom(updatedMailCopy, changedProperties);
            existingItem.IsBusy = mailUpdateSource == EntityUpdateSource.ClientUpdated;
        }
        finally
        {
            itemContainer.ThreadViewModel?.ResumeChildPropertyNotifications();
        }

        if (ShouldReinsertForChanges(appliedChanges))
        {
            var removeResult = RemoveMailCore(existingItem.MailCopy.UniqueId);
            RebuildMailLookupIndexes();
            existingItem.IsSelected = wasSelected;
            existingItem.IsBusy = mailUpdateSource == EntityUpdateSource.ClientUpdated || wasBusy;
            var addResult = AddMailCore(existingItem);
            return UpdateMailResult.For(removeResult.TouchedItems.Concat(addResult.TouchedItems).ToArray());
        }

        itemContainer.ThreadViewModel?.NotifyMailItemUpdated(existingItem, appliedChanges);
        return itemContainer.ThreadViewModel == null
            ? UpdateMailResult.For(existingItem)
            : UpdateMailResult.For(itemContainer.ThreadViewModel);
    }

    private void UpdateMailStateCore(MailStateChange updatedState, EntityUpdateSource mailUpdateSource)
    {
        var itemContainer = GetMailItemContainer(updatedState.UniqueId);
        if (itemContainer?.ItemViewModel == null)
        {
            return;
        }

        var existingItem = itemContainer.ItemViewModel;
        var appliedChanges = existingItem.ApplyStateChanges(updatedState.IsRead, updatedState.IsFlagged);
        existingItem.IsBusy = mailUpdateSource == EntityUpdateSource.ClientUpdated;

        itemContainer.ThreadViewModel?.NotifyMailItemUpdated(existingItem, appliedChanges);
    }

    private IMailListItem FindThreadableItem(string threadId, Guid? excludedUniqueId = null)
    {
        if (string.IsNullOrEmpty(threadId) || !_threadIdToItemsMap.TryGetValue(threadId, out var items))
        {
            return null;
        }

        foreach (var item in items)
        {
            if (excludedUniqueId.HasValue)
            {
                if (item is MailItemViewModel mail && mail.MailCopy.UniqueId == excludedUniqueId.Value)
                {
                    continue;
                }

                if (item is ThreadMailItemViewModel thread && thread.HasUniqueId(excludedUniqueId.Value))
                {
                    continue;
                }
            }

            return item;
        }

        return null;
    }

    private object GetGroupingKey(IMailListItem mailItem)
    {
        if (!AreGroupHeadersEnabled)
        {
            return MailListGroupKey.Groupless;
        }

        if (mailItem.IsPinned)
        {
            return MailListGroupKey.Pinned;
        }

        return SortingType == SortingOptionType.ReceiveDate
            ? new MailListGroupKey(false, mailItem.CreationDate.ToLocalTime().Date)
            : new MailListGroupKey(false, mailItem.FromName);
    }

    private bool ShouldReinsertForChanges(MailCopyChangeFlags changedProperties)
    {
        if (changedProperties == MailCopyChangeFlags.None)
        {
            return false;
        }

        if ((changedProperties & (MailCopyChangeFlags.ThreadId | MailCopyChangeFlags.IsPinned)) != 0)
        {
            return true;
        }

        return SortingType == SortingOptionType.ReceiveDate
            ? (changedProperties & MailCopyChangeFlags.CreationDate) != 0
            : (changedProperties & (MailCopyChangeFlags.FromName | MailCopyChangeFlags.FromAddress)) != 0;
    }

    private bool ShouldReinsertBeforeApplyingUpdate(MailItemViewModel existingItem, MailCopy updatedMailCopy, MailCopyChangeFlags changedProperties)
    {
        if (changedProperties == MailCopyChangeFlags.None)
        {
            return false;
        }

        if (changedProperties != MailCopyChangeFlags.All || ReferenceEquals(existingItem.MailCopy, updatedMailCopy))
        {
            return ShouldReinsertForChanges(changedProperties);
        }

        var structuralChanges = MailCopyChangeFlags.None;

        if (existingItem.MailCopy.ThreadId != updatedMailCopy.ThreadId)
        {
            structuralChanges |= MailCopyChangeFlags.ThreadId;
        }

        if (existingItem.MailCopy.IsPinned != updatedMailCopy.IsPinned)
        {
            structuralChanges |= MailCopyChangeFlags.IsPinned;
        }

        if (existingItem.MailCopy.CreationDate != updatedMailCopy.CreationDate)
        {
            structuralChanges |= MailCopyChangeFlags.CreationDate;
        }

        if (existingItem.MailCopy.FromName != updatedMailCopy.FromName)
        {
            structuralChanges |= MailCopyChangeFlags.FromName;
        }

        if (existingItem.MailCopy.FromAddress != updatedMailCopy.FromAddress)
        {
            structuralChanges |= MailCopyChangeFlags.FromAddress;
        }

        return ShouldReinsertForChanges(structuralChanges);
    }

    private void SortTopLevelItems()
    {
        _topLevelItems.Sort(_listComparer.Compare);

        foreach (var thread in _topLevelItems.OfType<ThreadMailItemViewModel>())
        {
            thread.IsSelected = thread.IsSelected || thread.ThreadEmails.Any(static child => child.IsSelected);
        }
    }

    private void RebuildGroups()
    {
        _itemToGroupMap.Clear();

        var groupedItems = _topLevelItems
            .GroupBy(GetGroupingKey)
            .OrderBy(static group => group.Key, _listComparer)
            .Select(group =>
            {
                var sortedItems = group.OrderBy(static item => item, _listComparer).ToList();
                return new WinoMailGroup(group.Key, sortedItems);
            })
            .ToList();

        MailItems.ReplaceAll(groupedItems);

        foreach (var mailGroup in MailItems)
        {
            foreach (var item in mailGroup)
            {
                _itemToGroupMap[item] = mailGroup;
            }
        }
    }

    private void RefreshGroupsIncrementally(IReadOnlyList<IMailListItem> touchedItems)
    {
        var touchedItemList = touchedItems?
            .Where(static item => item != null)
            .Distinct()
            .ToList() ?? [];

        if (touchedItemList.Count == 0)
        {
            return;
        }

        var removedItemsByGroup = new Dictionary<WinoMailGroup, List<IMailListItem>>();
        var changedGroups = new HashSet<WinoMailGroup>();
        var stableTouchedItems = new HashSet<IMailListItem>();

        foreach (var item in touchedItemList)
        {
            if (!_itemToGroupMap.TryRemove(item, out var group))
            {
                group = MailItems.FirstOrDefault(mailGroup => mailGroup.Contains(item));
            }

            if (group == null)
            {
                continue;
            }

            if (_topLevelItems.Contains(item) && Equals(group.Key, GetGroupingKey(item)))
            {
                _itemToGroupMap[item] = group;
                changedGroups.Add(group);
                stableTouchedItems.Add(item);
                continue;
            }

            if (!removedItemsByGroup.TryGetValue(group, out var removedItems))
            {
                removedItems = [];
                removedItemsByGroup[group] = removedItems;
            }

            removedItems.Add(item);
        }

        foreach (var (group, removedItems) in removedItemsByGroup)
        {
            group.RemoveRange(removedItems);

            if (group.Count == 0)
            {
                MailItems.Remove(group);
            }
        }

        foreach (var groupItems in touchedItemList
            .Where(_topLevelItems.Contains)
            .Where(item => !stableTouchedItems.Contains(item))
            .GroupBy(GetGroupingKey))
        {
            var group = GetOrCreateGroup(groupItems.Key);
            var mergedItems = group
                .Concat(groupItems)
                .Distinct()
                .OrderBy(static item => item, _listComparer)
                .ToList();

            group.ReplaceAll(mergedItems);

            foreach (var item in mergedItems)
            {
                _itemToGroupMap[item] = group;
            }
        }

        foreach (var group in changedGroups)
        {
            foreach (var item in stableTouchedItems.Where(group.Contains).ToList())
            {
                MoveItemToSortedPosition(group, item);
                _itemToGroupMap[item] = group;
            }
        }
    }

    private void MoveItemToSortedPosition(WinoMailGroup group, IMailListItem item)
    {
        var currentIndex = group.IndexOf(item);

        if (currentIndex < 0)
        {
            return;
        }

        var targetIndex = 0;

        for (var i = 0; i < group.Count; i++)
        {
            if (i == currentIndex)
            {
                continue;
            }

            if (_listComparer.Compare(item, group[i]) < 0)
            {
                break;
            }

            targetIndex++;
        }

        if (targetIndex != currentIndex)
        {
            group.Move(currentIndex, targetIndex);
        }
    }

    private WinoMailGroup GetOrCreateGroup(object groupKey)
    {
        var group = MailItems.FirstOrDefault(mailGroup => Equals(mailGroup.Key, groupKey));

        if (group == null)
        {
            group = new WinoMailGroup(groupKey, []);
            MailItems.Insert(GetGroupInsertIndex(groupKey), group);
        }

        return group;
    }

    private int GetGroupInsertIndex(object groupKey)
    {
        for (var i = 0; i < MailItems.Count; i++)
        {
            if (_listComparer.Compare(groupKey, MailItems[i].Key) < 0)
            {
                return i;
            }
        }

        return MailItems.Count;
    }

    private void RebuildIndexes()
    {
        ClearIndexes();
        RebuildMailLookupIndexes();
    }

    private void RebuildMailLookupIndexes()
    {
        ClearMailLookupIndexes();

        foreach (var item in _topLevelItems)
        {
            IndexTopLevelItem(item);
        }
    }

    private void IndexTopLevelItem(IMailListItem item)
    {
        foreach (var threadId in GetThreadIdsFromItem(item))
        {
            var list = _threadIdToItemsMap.GetOrAdd(threadId, static _ => []);
            if (!list.Contains(item))
            {
                list.Add(item);
            }
        }

        if (item is MailItemViewModel mail)
        {
            MailCopyIdHashSet.TryAdd(mail.MailCopy.UniqueId, true);
            _uniqueIdToMailItemMap[mail.MailCopy.UniqueId] = mail;
            return;
        }

        if (item is ThreadMailItemViewModel thread)
        {
            foreach (var child in thread.ThreadEmails)
            {
                MailCopyIdHashSet.TryAdd(child.MailCopy.UniqueId, true);
                _uniqueIdToMailItemMap[child.MailCopy.UniqueId] = child;
                _uniqueIdToThreadMap[child.MailCopy.UniqueId] = thread;
            }
        }
    }

    private static IEnumerable<string> GetThreadIdsFromItem(IMailListItem item)
    {
        if (item is MailItemViewModel mailItem && !string.IsNullOrEmpty(mailItem.MailCopy.ThreadId))
        {
            yield return mailItem.MailCopy.ThreadId;
            yield break;
        }

        if (item is ThreadMailItemViewModel threadItem)
        {
            foreach (var threadId in threadItem.ThreadEmails
                         .Select(static email => email.MailCopy.ThreadId)
                         .Where(static threadId => !string.IsNullOrEmpty(threadId))
                         .Distinct(StringComparer.Ordinal))
            {
                yield return threadId;
            }
        }
    }

    private void ClearIndexes()
    {
        ClearMailLookupIndexes();
        _itemToGroupMap.Clear();
    }

    private void ClearMailLookupIndexes()
    {
        MailCopyIdHashSet.Clear();
        _threadIdToItemsMap.Clear();
        _uniqueIdToMailItemMap.Clear();
        _uniqueIdToThreadMap.Clear();
    }

    private void DetachThreadHandlers()
    {
        foreach (var thread in _topLevelItems.OfType<ThreadMailItemViewModel>())
        {
            thread.UnregisterThreadEmailPropertyChangedHandlers();
        }
    }

    private IEnumerable<MailItemViewModel> EnumerateLeafItems()
    {
        foreach (var item in _topLevelItems)
        {
            if (item is MailItemViewModel mailItem)
            {
                yield return mailItem;
            }
            else if (item is ThreadMailItemViewModel threadItem)
            {
                foreach (var threadMailItem in threadItem.ThreadEmails)
                {
                    yield return threadMailItem;
                }
            }
        }
    }

    private IEnumerable<IMailListItem> EnumerateTopLevelAndLeafItems()
    {
        foreach (var item in _topLevelItems)
        {
            yield return item;

            if (item is ThreadMailItemViewModel thread)
            {
                foreach (var child in thread.ThreadEmails)
                {
                    yield return child;
                }
            }
        }
    }

    private async Task NotifySelectionChangesAsync()
    {
        await ExecuteUIThread(() =>
        {
            OnPropertyChanged(nameof(IsAllItemsSelected));
            OnPropertyChanged(nameof(IsAllSelected));
            OnPropertyChanged(nameof(SelectedItemsCount));
            OnPropertyChanged(nameof(HasSingleItemSelected));

            ItemSelectionChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Task ExecuteUIThread(Action action)
    {
        if (CoreDispatcher == null)
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
}
