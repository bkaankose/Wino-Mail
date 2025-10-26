using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Wino.Core.Domain.Entities.Mail;
using Wino.Mail.ViewModels.Data;

namespace Wino.Mail.ViewModels.Collections;

/// <summary>
/// Grouping options for emails
/// </summary>
public enum EmailGroupingType
{
    ByFromName,
    ByDate
}

/// <summary>
/// Sorting options for emails within groups
/// </summary>
public enum EmailSortDirection
{
    Ascending,
    Descending
}

/// <summary>
/// Collection that automatically groups MailItemViewModels with ThreadMailItemViewModels in a flat structure for ItemsView.
/// All emails are in the same flat list with proper selection support. Thread emails are placed consecutively after their expander.
/// </summary>
public partial class GroupedEmailCollection : ObservableObject, IRecipient<PropertyChangedMessage<bool>>, IDisposable
{
    public event EventHandler SelectionChanged;

    private readonly ObservableCollection<MailItemViewModel> _sourceItems = [];
    private readonly Dictionary<string, GroupHeaderBase> _groupHeaders = [];
    private readonly Dictionary<string, int> _groupHeaderIndexCache = [];
    private readonly Dictionary<string, List<object>> _groupItems = [];
    private readonly Dictionary<string, ThreadMailItemViewModel> _threadExpanders = [];
    private readonly HashSet<Guid> _mailCopyIdHashSet = [];
    private readonly HashSet<MailItemViewModel> _selectedVisibleItems = [];
    private bool _disposed;
    private bool _isUpdating;

    [ObservableProperty]
    private EmailGroupingType groupingType = EmailGroupingType.ByDate;

    [ObservableProperty]
    private EmailSortDirection sortDirection = EmailSortDirection.Descending;

    // Tracks the number of currently selected visible mail items. Notify derived bools when changed.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedItems))]
    [NotifyPropertyChangedFor(nameof(HasSingleItemSelected))]
    [NotifyPropertyChangedFor(nameof(HasMultipleItemsSelected))]
    [NotifyPropertyChangedFor(nameof(IsAllItemsSelected))]
    public partial int SelectedVisibleCount { get; set; }

    /// <summary>
    /// Indicates whether there are any selected visible items.
    /// </summary>
    public bool HasSelectedItems => SelectedVisibleCount > 0;

    /// <summary>
    /// Indicates whether there is exactly one selected visible item.
    /// </summary>
    public bool HasSingleItemSelected => SelectedVisibleCount == 1;

    /// <summary>
    /// Indicates whether there are multiple selected visible items.
    /// </summary>
    public bool HasMultipleItemsSelected => SelectedVisibleCount > 1;

    /// <summary>
    /// Indicates whether all mail items are currently selected.
    /// Counts all mail items including those in threads, regardless of thread expansion state.
    /// </summary>
    public bool IsAllItemsSelected
    {
        get
        {
            var totalMailItems = _sourceItems.Count;
            if (totalMailItems == 0) return false;

            var selectedCount = 0;

            // Count selected standalone emails (not in threads)
            selectedCount += _sourceItems.Count(e => e.IsSelected && !e.IsDisplayedInThread);

            // Count selected emails in threads
            foreach (var expander in _threadExpanders.Values)
            {
                if (expander.IsSelected)
                {
                    // If thread is selected, all emails in the thread are considered selected
                    selectedCount += expander.ThreadEmails.Count;
                }
                else
                {
                    // If thread is not selected, count only individually selected emails within the thread
                    selectedCount += expander.ThreadEmails.Count(e => e.IsSelected);
                }
            }

            return selectedCount == totalMailItems;
        }
    }

    public GroupedEmailCollection()
    {
        // Create a flat collection for ItemsView with headers, expanders and emails mixed
        Items = [];

        // Subscribe to source collection changes to update grouping
        _sourceItems.CollectionChanged += OnSourceItemsChanged;

        // Register for PropertyChanged messages
        WeakReferenceMessenger.Default.Register<PropertyChangedMessage<bool>>(this);

        RefreshGrouping();
    }

    /// <summary>
    /// Flat observable collection containing group headers, thread expanders, and email items for ItemsView binding.
    /// Structure: GroupHeader -> [ThreadExpander -> ThreadEmail1, ThreadEmail2, ...] -> StandaloneEmail1 -> StandaloneEmail2
    /// </summary>
    public ObservableCollection<object> Items { get; }

    /// <summary>
    /// Total number of emails across all groups
    /// </summary>
    public int TotalCount => _sourceItems.Count;

    /// <summary>
    /// Total number of unread emails across all groups
    /// </summary>
    public int TotalUnreadCount => _sourceItems.Count(e => e.MailCopy?.IsRead == false);

    /// <summary>
    /// HashSet containing unique IDs of all mail copies in the collection for pagination tracking
    /// </summary>
    public HashSet<Guid> MailCopyIdHashSet => _mailCopyIdHashSet;

    /// <summary>
    /// Gets all email items across all groups as a flat collection
    /// </summary>
    public IEnumerable<MailItemViewModel> AllItems => _sourceItems;

    /// <summary>
    /// Gets all currently selected visible email items in the UI.
    /// This collection is automatically maintained by tracking PropertyChanged events.
    /// </summary>
    public IReadOnlyCollection<MailItemViewModel> SelectedVisibleItems => _selectedVisibleItems;

    /// <summary>
    /// Gets all currently selected email items.
    /// Includes:
    /// - Standalone mail items where IsSelected=true
    /// - Mail items inside threads where the mail item's IsSelected=true (regardless of thread expansion)
    /// - All mail items inside a thread where the thread's IsSelected=true
    /// </summary>
    public IEnumerable<MailItemViewModel> SelectedItems
    {
        get
        {
            var selectedItems = new List<MailItemViewModel>();

            // Add selected standalone emails (not in threads)
            selectedItems.AddRange(_sourceItems.Where(e => e.IsSelected && !e.IsDisplayedInThread));

            // Process thread expanders
            foreach (var expander in _threadExpanders.Values)
            {
                if (expander.IsSelected)
                {
                    // If thread is selected, add all emails in the thread
                    selectedItems.AddRange(expander.ThreadEmails);
                }
                else
                {
                    // If thread is not selected, add only individually selected emails within the thread
                    selectedItems.AddRange(expander.ThreadEmails.Where(e => e.IsSelected));
                }
            }

            return selectedItems;
        }
    }

    /// <summary>
    /// Gets the count of all currently selected email items.
    /// Counts:
    /// - Standalone mail items where IsSelected=true
    /// - Mail items inside threads where the mail item's IsSelected=true (regardless of thread expansion)
    /// - All mail items inside a thread where the thread's IsSelected=true
    /// </summary>
    public int SelectedItemsCount => SelectedItems.Count();

    /// <summary>
    /// Gets the number of visible email items (excluding group headers).
    /// For threads, counts the expander as 1 if collapsed, or all thread emails if expanded.
    /// </summary>
    public int Count
    {
        get
        {
            int count = 0;

            foreach (var item in Items)
            {
                switch (item)
                {
                    case GroupHeaderBase:
                        // Skip group headers
                        break;
                    case ThreadMailItemViewModel thread:
                        count += thread.ThreadEmails.Count;
                        break;
                    case MailItemViewModel:
                        count += 1;
                        break;
                }
            }
            return count;
        }
    }

    /// <summary>
    /// Handles PropertyChanged messages for thread expansion and mail item selection
    /// </summary>
    public void Receive(PropertyChangedMessage<bool> message)
    {
        // Only handle IsThreadExpanded property changes from ThreadMailItemViewModel
        if (_isUpdating)
            return;

        if (message.PropertyName == nameof(ThreadMailItemViewModel.IsThreadExpanded) && message.Sender is ThreadMailItemViewModel expander)
        {
            HandleThreadExpansion(expander);
        }
        else if (message.PropertyName == nameof(MailItemViewModel.IsSelected) && message.Sender is MailItemViewModel mailItem)
        {
            HandleMailItemSelectionChanged(mailItem, message.NewValue);
        }
        else if (message.PropertyName == nameof(ThreadMailItemViewModel.IsSelected) && message.Sender is ThreadMailItemViewModel threadExpander)
        {
            HandleThreadSelectionChanged(threadExpander, message.NewValue);
        }
    }

    private void HandleMailItemSelectionChanged(MailItemViewModel mailItem, bool isSelected)
    {
        bool selectionChanged = false;

        if (isSelected)
        {
            // Add to selected items if it's visible in the UI
            if (Items.Contains(mailItem))
            {
                if (_selectedVisibleItems.Add(mailItem))
                {
                    SelectedVisibleCount = _selectedVisibleItems.Count;
                    OnPropertyChanged(nameof(SelectedVisibleItems));
                    selectionChanged = true;
                }
            }
        }
        else
        {
            // Remove from selected items
            if (_selectedVisibleItems.Remove(mailItem))
            {
                SelectedVisibleCount = _selectedVisibleItems.Count;
                OnPropertyChanged(nameof(SelectedVisibleItems));
                selectionChanged = true;
            }
        }

        if (selectionChanged)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void HandleThreadSelectionChanged(ThreadMailItemViewModel threadExpander, bool isSelected)
    {
        // When a thread expander's selection changes, it affects the selection state of all emails in that thread
        // We need to notify that the "all items selected" state might have changed
        OnPropertyChanged(nameof(IsAllItemsSelected));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Registers a mail item to track its selection state when added to the visible UI
    /// </summary>
    private void RegisterMailItemForSelectionTracking(MailItemViewModel mailItem)
    {
        if (mailItem == null)
            return;

        // Subscribe to property changed to track IsSelected changes
        mailItem.PropertyChanged += MailItem_PropertyChanged;

        // If the item is already selected, add it to the tracking set
        if (mailItem.IsSelected && Items.Contains(mailItem))
        {
            if (_selectedVisibleItems.Add(mailItem))
            {
                SelectedVisibleCount = _selectedVisibleItems.Count;
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>
    /// Unregisters a mail item from selection tracking when removed from the visible UI
    /// </summary>
    private void UnregisterMailItemFromSelectionTracking(MailItemViewModel mailItem)
    {
        if (mailItem == null)
            return;

        // Unsubscribe from property changed
        mailItem.PropertyChanged -= MailItem_PropertyChanged;

        // Remove from selected items tracking
        if (_selectedVisibleItems.Remove(mailItem))
        {
            SelectedVisibleCount = _selectedVisibleItems.Count;
            OnPropertyChanged(nameof(SelectedVisibleItems));
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void MailItem_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is MailItemViewModel mailItem && e.PropertyName == nameof(MailItemViewModel.IsSelected))
        {
            HandleMailItemSelectionChanged(mailItem, mailItem.IsSelected);
        }
    }

    private void HandleThreadExpansion(ThreadMailItemViewModel expander)
    {
        _isUpdating = true;
        try
        {
            var expanderIndex = Items.IndexOf(expander);
            if (expanderIndex == -1)
                return;

            if (expander.IsThreadExpanded)
            {
                // Add thread emails after the expander
                var insertIndex = expanderIndex + 1;
                var sortedThreadEmails = SortDirection == EmailSortDirection.Descending
                    ? expander.ThreadEmails.OrderByDescending(e => e.MailCopy.CreationDate).ToList()
                    : expander.ThreadEmails.OrderBy(e => e.MailCopy?.CreationDate).ToList();

                foreach (var email in sortedThreadEmails)
                {
                    Items.Insert(insertIndex, email);
                    RegisterMailItemForSelectionTracking(email);
                    insertIndex++;
                }

                UpdateHeaderIndicesAfterInsertion(expanderIndex + 1, expander.EmailCount);
            }
            else
            {
                // Remove thread emails from UI
                foreach (var email in expander.ThreadEmails.ToList())
                {
                    var emailIndex = Items.IndexOf(email);
                    if (emailIndex >= 0)
                    {
                        UnregisterMailItemFromSelectionTracking(email);
                        Items.RemoveAt(emailIndex);
                        UpdateHeaderIndicesAfterRemoval(emailIndex);
                    }
                }
            }

            // Notify that the "all items selected" state might have changed due to visible item count change
            OnPropertyChanged(nameof(IsAllItemsSelected));
        }
        finally
        {
            _isUpdating = false;
        }
    }

    /// <summary>
    /// Adds an email to the collection which will automatically group it.
    /// If an email with the same ThreadId exists, adds it to the existing thread or creates a new thread.
    /// </summary>
    public void AddEmail(MailItemViewModel email)
    {
        if (email?.MailCopy == null)
            return;

        // Add to unique ID tracking
        _mailCopyIdHashSet.Add(email.MailCopy.UniqueId);

        _isUpdating = true;
        try
        {
            // Check if this email belongs to a thread
            if (!string.IsNullOrEmpty(email.MailCopy.ThreadId))
            {
                // Look for existing emails with the same ThreadId
                var existingThreadEmails = _sourceItems
                    .Where(e => e.MailCopy?.ThreadId == email.MailCopy.ThreadId)
                    .ToList();

                var existingExpander = _threadExpanders.GetValueOrDefault(email.MailCopy.ThreadId);

                if (existingThreadEmails.Any() || existingExpander != null)
                {
                    // Add to existing thread
                    if (existingExpander == null)
                    {
                        // Create thread expander for the first time (existing emails become part of thread)
                        existingExpander = new ThreadMailItemViewModel(email.MailCopy.ThreadId);
                        _threadExpanders[email.MailCopy.ThreadId] = existingExpander;

                        // Remove existing standalone emails from UI and add them to the thread
                        foreach (var existingEmail in existingThreadEmails)
                        {
                            RemoveEmailFromUI(existingEmail);
                            existingExpander.AddEmail(existingEmail);
                            existingEmail.IsDisplayedInThread = true;
                        }
                    }

                    // Add the new email to the thread
                    existingExpander.AddEmail(email);
                    email.IsDisplayedInThread = true;

                    // Add to source collection
                    var insertIndex = FindInsertionIndex(email);
                    _sourceItems.Insert(insertIndex, email);

                    // Add thread expander and all emails to UI in correct positions
                    RefreshThreadInUI(existingExpander);
                }
                else
                {
                    // First email with this ThreadId - treat as standalone for now
                    email.IsDisplayedInThread = false;
                    var insertIndex = FindInsertionIndex(email);
                    _sourceItems.Insert(insertIndex, email);
                    AddEmailToUI(email);
                }
            }
            else
            {
                // No ThreadId - standalone email
                email.IsDisplayedInThread = false;
                var insertIndex = FindInsertionIndex(email);
                _sourceItems.Insert(insertIndex, email);
                AddEmailToUI(email);
            }

            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(TotalUnreadCount));
        }
        finally
        {
            _isUpdating = false;
        }
    }

    /// <summary>
    /// Removes an email from the collection.
    /// If the email is part of a thread and removing it would leave only 1 item in the thread,
    /// the thread is converted back to a single email.
    /// </summary>
    public void RemoveEmail(MailItemViewModel email)
    {
        if (email?.MailCopy == null)
            return;

        RemoveEmailByMailCopy(email.MailCopy);
    }

    /// <summary>
    /// Removes an email from the collection based on MailCopy.
    /// The mail copy might be in a thread where it's not visible in the UI items.
    /// In that case, it will be removed from the all items source and the thread.
    /// </summary>
    public void RemoveEmailByMailCopy(MailCopy mailCopy)
    {
        if (mailCopy == null)
            return;

        // Remove from unique ID tracking
        _mailCopyIdHashSet.Remove(mailCopy.UniqueId);

        _isUpdating = true;
        try
        {
            // Find the email in the source collection
            var email = _sourceItems.FirstOrDefault(e => e.MailCopy.UniqueId == mailCopy.UniqueId);

            if (email == null)
                return; // Email not found

            var threadId = mailCopy.ThreadId;

            // Remove from source collection
            _sourceItems.Remove(email);

            if (!string.IsNullOrEmpty(threadId) && _threadExpanders.TryGetValue(threadId, out var expander))
            {
                // Remove from thread
                expander.RemoveEmail(email);
                email.IsDisplayedInThread = false;

                // Remove email from UI if it's visible (only if thread is expanded)
                RemoveEmailFromUI(email);

                // Check if thread now has only 1 email - convert back to standalone
                if (expander.EmailCount == 1)
                {
                    var remainingEmail = expander.ThreadEmails.First();

                    // Remove thread expander and remaining email from UI
                    RemoveThreadFromUI(expander);

                    // Set remaining email as no longer displayed in thread
                    remainingEmail.IsDisplayedInThread = false;

                    // Remove thread expander tracking
                    _threadExpanders.Remove(threadId);
                    expander.Dispose();

                    // Add remaining email as standalone
                    AddEmailToUI(remainingEmail);
                }
                else if (expander.EmailCount == 0)
                {
                    // Thread is empty - remove completely
                    RemoveThreadFromUI(expander);
                    _threadExpanders.Remove(threadId);
                    expander.Dispose();
                }
                else
                {
                    // Thread still has multiple emails - refresh its position
                    RefreshThreadInUI(expander);
                }
            }
            else
            {
                // Standalone email
                email.IsDisplayedInThread = false;
                RemoveEmailFromUI(email);
            }

            // Update group headers
            UpdateGroupAfterChanges();

            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(TotalUnreadCount));
        }
        finally
        {
            _isUpdating = false;
        }
    }

    /// <summary>
    /// Adds multiple emails to the collection efficiently using bulk operations
    /// </summary>
    public void AddEmails(IEnumerable<MailItemViewModel> emails)
    {
        var emailList = emails.Where(e => e?.MailCopy != null).ToList();
        if (!emailList.Any())
            return;

        _isUpdating = true;
        try
        {
            // Add to unique ID tracking
            foreach (var email in emailList)
            {
                _mailCopyIdHashSet.Add(email.MailCopy.UniqueId);
            }

            // For bulk loading, add to source and use incremental refresh to preserve selection
            foreach (var email in emailList)
            {
                var insertIndex = FindInsertionIndex(email);
                _sourceItems.Insert(insertIndex, email);
            }

            // Use incremental refresh instead of full refresh to preserve selection
            IncrementalRefreshGrouping(emailList);

            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(TotalUnreadCount));
        }
        finally
        {
            _isUpdating = false;
        }
    }

    /// <summary>
    /// Clears all emails, threads, and headers
    /// </summary>
    public void Clear()
    {
        _isUpdating = true;
        try
        {
            // Unregister all mail items from selection tracking
            foreach (var item in Items)
            {
                if (item is MailItemViewModel mailItem)
                {
                    UnregisterMailItemFromSelectionTracking(mailItem);
                }
            }

            // Clear selected items tracking
            var hadSelectedItems = _selectedVisibleItems.Count > 0;
            _selectedVisibleItems.Clear();
            SelectedVisibleCount = 0;

            // Reset IsDisplayedInThread for all emails before clearing
            foreach (var email in _sourceItems)
            {
                email.IsDisplayedInThread = false;
            }

            // Dispose all thread expanders
            foreach (var expander in _threadExpanders.Values)
            {
                expander.Dispose();
            }

            _sourceItems.Clear();
            Items.Clear();
            _groupHeaders.Clear();
            _groupHeaderIndexCache.Clear();
            _groupItems.Clear();
            _threadExpanders.Clear();
            _mailCopyIdHashSet.Clear();

            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(TotalUnreadCount));
            OnPropertyChanged(nameof(SelectedVisibleItems));
            OnPropertyChanged(nameof(IsAllItemsSelected));

            if (hadSelectedItems)
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _isUpdating = false;
        }
    }

    /// <summary>
    /// Changes the grouping type and rebuilds the collection
    /// </summary>
    public void ChangeGrouping(EmailGroupingType newGroupingType, EmailSortDirection newSortDirection = EmailSortDirection.Descending)
    {
        if (GroupingType == newGroupingType && SortDirection == newSortDirection)
            return;

        GroupingType = newGroupingType;
        SortDirection = newSortDirection;
        RefreshGrouping();
    }

    /// <summary>
    /// Manually refreshes the grouping (useful after bulk operations)
    /// </summary>
    public void RefreshGrouping()
    {
        _isUpdating = true;
        try
        {
            // Unregister all mail items before clearing
            foreach (var item in Items)
            {
                if (item is MailItemViewModel mailItem)
                {
                    UnregisterMailItemFromSelectionTracking(mailItem);
                }
            }

            // Clear UI items but preserve source and expanders
            Items.Clear();
            _groupHeaders.Clear();
            _groupHeaderIndexCache.Clear();
            _groupItems.Clear();
            var hadSelectedItems = _selectedVisibleItems.Count > 0;
            _selectedVisibleItems.Clear();
            SelectedVisibleCount = 0;

            if (!_sourceItems.Any())
            {
                if (hadSelectedItems)
                {
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
                }
                return;
            }

            // Rebuild thread expanders based on current emails
            RebuildThreadExpanders();

            // Group all items (standalone emails and thread expanders) by criteria
            var allItems = new List<object>();

            // Add standalone emails (emails without threads or not in any expander)
            var standaloneEmails = _sourceItems
                .Where(e => string.IsNullOrEmpty(e.MailCopy?.ThreadId) ||
                           !_threadExpanders.ContainsKey(e.MailCopy.ThreadId))
                .ToList();

            allItems.AddRange(standaloneEmails.Cast<object>());
            allItems.AddRange(_threadExpanders.Values.Cast<object>());

            // Group by criteria
            var groupedItems = allItems
                .GroupBy(item => GetGroupKeyForItem(item))
                .OrderBy(g => g.Key, GetGroupComparer());

            var currentIndex = 0;

            // Process each group
            foreach (var group in groupedItems)
            {
                // Create group header
                var groupHeader = CreateGroupHeader(group.Key);
                _groupHeaders[group.Key] = groupHeader;
                _groupHeaderIndexCache[group.Key] = currentIndex;

                // Sort items within the group
                var sortedGroupItems = SortDirection == EmailSortDirection.Descending
                    ? group.OrderByDescending(GetEffectiveDate).ToList()
                    : group.OrderBy(GetEffectiveDate).ToList();

                _groupItems[group.Key] = sortedGroupItems;

                // Add header to flat collection
                Items.Add(groupHeader);
                currentIndex++;

                // Add all items in this group to flat collection
                foreach (var item in sortedGroupItems)
                {
                    if (item is ThreadMailItemViewModel expander)
                    {
                        // Add expander
                        Items.Add(expander);
                        currentIndex++;

                        // Only add thread emails if the thread is expanded
                        if (expander.IsThreadExpanded)
                        {
                            var sortedThreadEmails = SortDirection == EmailSortDirection.Descending
                                ? expander.ThreadEmails.OrderByDescending(e => e.MailCopy?.CreationDate).ToList()
                                : expander.ThreadEmails.OrderBy(e => e.MailCopy?.CreationDate).ToList();

                            foreach (var threadEmail in sortedThreadEmails)
                            {
                                Items.Add(threadEmail);
                                RegisterMailItemForSelectionTracking(threadEmail);
                                currentIndex++;
                            }
                        }
                    }
                    else if (item is MailItemViewModel email)
                    {
                        // Add standalone email
                        Items.Add(email);
                        RegisterMailItemForSelectionTracking(email);
                        currentIndex++;
                    }
                }
            }

            // Update group header counts
            UpdateAllGroupHeaderCounts();

            // Notify that the "all items selected" state might have changed due to visible items rebuild
            OnPropertyChanged(nameof(IsAllItemsSelected));

            if (hadSelectedItems)
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            _isUpdating = false;
        }
    }

    /// <summary>
    /// Incrementally adds new emails to the collection without clearing existing items
    /// </summary>
    private void IncrementalRefreshGrouping(IList<MailItemViewModel> newEmails)
    {
        _isUpdating = true;
        try
        {
            if (!newEmails.Any())
                return;

            // Update thread expanders with any new emails that should be threaded
            UpdateThreadExpandersForNewEmails(newEmails);

            // Process each new email
            foreach (var email in newEmails)
            {
                // Skip if it's already displayed in a thread
                if (email.IsDisplayedInThread)
                    continue;

                // Determine the group key for this email
                var groupKey = GetGroupKeyForItem(email);

                // Get or create the group header
                var groupHeader = GetOrCreateGroupHeader(groupKey);

                // Find where this email should be inserted in the UI
                var insertPosition = FindUIInsertionPosition(email, groupKey);

                // Insert the email at the correct position
                Items.Insert(insertPosition, email);
                RegisterMailItemForSelectionTracking(email);

                // Update the group items list
                if (!_groupItems.ContainsKey(groupKey))
                {
                    _groupItems[groupKey] = new List<object>();
                }

                // Insert in the group items list maintaining sort order
                var groupItems = _groupItems[groupKey];
                var groupInsertIndex = FindGroupInsertionIndex(email, groupItems);
                groupItems.Insert(groupInsertIndex, email);

                // If this is the first item in a new group, we need to add the header
                if (groupItems.Count == 1)
                {
                    // Find where to insert the header
                    var headerInsertPosition = FindHeaderInsertionPosition(groupKey, groupHeader);
                    Items.Insert(headerInsertPosition, groupHeader);
                    _groupHeaderIndexCache[groupKey] = headerInsertPosition;

                    // Update all subsequent header indices
                    UpdateSubsequentHeaderIndices(groupKey, 1);
                }
            }

            // Update group header counts for affected groups
            UpdateGroupHeaderCountsForNewEmails(newEmails);
        }
        finally
        {
            _isUpdating = false;
        }
    }

    /// <summary>
    /// Rebuilds thread expanders based on current source emails
    /// </summary>
    private void RebuildThreadExpanders()
    {
        // Group emails by ThreadId
        var threadGroups = _sourceItems
            .Where(e => !string.IsNullOrEmpty(e.MailCopy?.ThreadId))
            .GroupBy(e => e.MailCopy!.ThreadId!)
            .Where(g => g.Count() >= 2) // Only create threads with 2+ emails
            .ToList();

        // Remove expanders for threads that no longer have 2+ emails
        var expandersToRemove = _threadExpanders.Keys
            .Where(threadId => !threadGroups.Any(g => g.Key == threadId))
            .ToList();

        foreach (var threadId in expandersToRemove)
        {
            // Set emails back to not displayed in thread before removing expander
            foreach (var email in _threadExpanders[threadId].ThreadEmails)
            {
                email.IsDisplayedInThread = false;
            }

            _threadExpanders[threadId].Dispose();
            _threadExpanders.Remove(threadId);
        }

        // Create or update expanders for threads with 2+ emails
        foreach (var threadGroup in threadGroups)
        {
            if (!_threadExpanders.TryGetValue(threadGroup.Key, out var threadExpander))
            {
                threadExpander = new ThreadMailItemViewModel(threadGroup.Key);
                _threadExpanders[threadGroup.Key] = threadExpander;
            }

            // Clear and re-add emails to ensure consistency
            var currentEmails = threadExpander.ThreadEmails.ToList();
            foreach (var email in currentEmails)
            {
                threadExpander.RemoveEmail(email);
                email.IsDisplayedInThread = false;
            }

            foreach (var email in threadGroup)
            {
                threadExpander.AddEmail(email);
                email.IsDisplayedInThread = true;
            }
        }

        // Set standalone emails to not displayed in thread
        var standaloneEmails = _sourceItems
            .Where(e => string.IsNullOrEmpty(e.MailCopy?.ThreadId) ||
                       !_threadExpanders.ContainsKey(e.MailCopy.ThreadId))
            .ToList();

        foreach (var email in standaloneEmails)
        {
            email.IsDisplayedInThread = false;
        }
    }

    public int IndexOf(object item) => Items.IndexOf(item);

    private void RefreshThreadInUI(ThreadMailItemViewModel expander)
    {
        // Remove thread completely from UI
        RemoveThreadFromUI(expander);

        // Find correct position for thread expander based on latest email
        var groupKey = GetGroupKeyForItem(expander);
        AddThreadToUI(expander, groupKey);
    }

    public MailItemContainer GetMailItemContainer(Guid uniqueId)
    {
        // First, search in standalone mail items (not displayed in threads)
        var standaloneMailItem = _sourceItems.FirstOrDefault(item =>
            item.MailCopy.UniqueId == uniqueId && !item.IsDisplayedInThread);

        if (standaloneMailItem != null)
        {
            // Check if the standalone item is visible in the UI
            var isItemVisible = Items.Contains(standaloneMailItem);

            return new MailItemContainer(standaloneMailItem)
            {
                IsItemVisible = isItemVisible,
                IsThreadVisible = false // Not a threaded item
            };
        }

        // Search in thread expanders for threaded mail items
        foreach (var threadExpander in _threadExpanders.Values)
        {
            if (threadExpander.HasUniqueId(uniqueId))
            {
                // Find the specific mail item within the thread
                var threadMailItem = threadExpander.ThreadEmails.FirstOrDefault(email =>
                    email.MailCopy.UniqueId == uniqueId);

                if (threadMailItem != null)
                {
                    // Check visibility: thread expander must be visible, and for individual item visibility,
                    // the thread must be expanded and the item must be in the visible Items collection
                    var isThreadVisible = Items.Contains(threadExpander);
                    var isItemVisible = isThreadVisible && threadExpander.IsThreadExpanded && Items.Contains(threadMailItem);

                    return new MailItemContainer(threadMailItem, threadExpander)
                    {
                        IsItemVisible = isItemVisible,
                        IsThreadVisible = isThreadVisible
                    };
                }
            }
        }

        // Item not found
        return null;
    }

    /// <summary>
    /// Gets the next item in the UI list based on the given MailCopy.
    /// If the next item is a thread, the thread will be expanded and the first item in the thread returned.
    /// </summary>
    /// <param name="mailCopy">The mail copy to find the next item for.</param>
    /// <returns>The next mail item in the UI list, or null if no next item exists.</returns>
    public MailItemViewModel GetNextItem(MailCopy mailCopy)
    {
        if (mailCopy == null)
            return null;

        _isUpdating = true;
        try
        {
            // Find the current item in the UI Items collection
            var currentItemIndex = -1;
            MailItemViewModel currentMailItem = null;
            ThreadMailItemViewModel currentThread = null;

            // First, try to find the item as a standalone email in the Items collection
            for (int i = 0; i < Items.Count; i++)
            {
                var item = Items[i];

                if (item is MailItemViewModel mailItem && mailItem.MailCopy.UniqueId == mailCopy.UniqueId)
                {
                    currentItemIndex = i;
                    currentMailItem = mailItem;
                    break;
                }
            }

            // If not found as standalone, check if it's in a thread
            if (currentItemIndex == -1)
            {
                // Find the thread that contains this mail
                foreach (var expander in _threadExpanders.Values)
                {
                    if (expander.HasUniqueId(mailCopy.UniqueId))
                    {
                        // Find the thread expander in the Items collection
                        currentItemIndex = Items.IndexOf(expander);
                        currentThread = expander;

                        // If thread is expanded, find the specific mail item within the thread
                        if (expander.IsThreadExpanded)
                        {
                            var threadMailItem = expander.ThreadEmails.FirstOrDefault(e => e.MailCopy.UniqueId == mailCopy.UniqueId);
                            if (threadMailItem != null)
                            {
                                currentItemIndex = Items.IndexOf(threadMailItem);
                                currentMailItem = threadMailItem;
                            }
                        }

                        break;
                    }
                }
            }

            // If we still haven't found the item, it's not in the UI
            if (currentItemIndex == -1)
                return null;

            // Look for the next item in the Items collection
            for (int i = currentItemIndex + 1; i < Items.Count; i++)
            {
                var nextItem = Items[i];

                // Skip group headers
                if (nextItem is GroupHeaderBase)
                    continue;

                // If next item is a mail item, return it
                if (nextItem is MailItemViewModel nextMailItem)
                {
                    return nextMailItem;
                }

                // If next item is a thread expander, expand it and return the first item
                if (nextItem is ThreadMailItemViewModel threadExpander)
                {
                    // Expand the thread if not already expanded
                    if (!threadExpander.IsThreadExpanded)
                    {
                        threadExpander.IsThreadExpanded = true;
                    }

                    // Return the first item in the thread (latest email)
                    var sortedThreadEmails = SortDirection == EmailSortDirection.Descending
                        ? threadExpander.ThreadEmails.OrderByDescending(e => e.MailCopy?.CreationDate).ToList()
                        : threadExpander.ThreadEmails.OrderBy(e => e.MailCopy?.CreationDate).ToList();

                    return sortedThreadEmails.FirstOrDefault();
                }
            }

            // No next item found
            return null;
        }
        finally
        {
            _isUpdating = false;
        }
    }

    /// <summary>
    /// Searches for all mail items with a specific FromAddress and toggles their ThumbnailUpdatedEvent property.
    /// This will notify the UI to update thumbnails for all matching items.
    /// </summary>
    /// <param name="fromAddress">The email address to search for in FromAddress property.</param>
    public void UpdateThumbnailsForAddress(string fromAddress)
    {
        if (string.IsNullOrEmpty(fromAddress)) return;

        // Search through all source items (includes both standalone and threaded emails)
        foreach (var mailItem in _sourceItems)
        {
            if (string.Equals(mailItem.MailCopy.FromAddress, fromAddress, StringComparison.OrdinalIgnoreCase))
            {
                // Toggle the ThumbnailUpdatedEvent to notify the UI
                mailItem.ThumbnailUpdatedEvent = !mailItem.ThumbnailUpdatedEvent;
            }
        }
    }

    /// <summary>
    /// Selects all mail items in the collection.
    /// Includes standalone mail items and all mail items inside threads, regardless of thread expansion state.
    /// </summary>
    /// <returns>The number of items that were selected.</returns>
    public int SelectAll()
    {
        var initialSelectedCount = SelectedItems.Count();

        // Select all standalone emails (not in threads)
        foreach (var mailItem in _sourceItems.Where(e => !e.IsDisplayedInThread))
        {
            mailItem.IsSelected = true;
        }

        // Select all thread expanders (which automatically selects all emails within them)
        foreach (var expander in _threadExpanders.Values)
        {
            expander.IsSelected = true;
        }

        SelectedVisibleCount = _selectedVisibleItems.Count;

        var finalSelectedCount = SelectedItems.Count();
        var selectedCount = finalSelectedCount - initialSelectedCount;

        if (selectedCount > 0)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        return selectedCount;
    }

    /// <summary>
    /// Clears the selection of all mail items in the collection.
    /// Includes standalone mail items and all mail items inside threads, regardless of thread expansion state.
    /// </summary>
    /// <returns>The number of items that were deselected.</returns>
    public int ClearSelections()
    {
        var initialSelectedCount = SelectedItems.Count();

        // Deselect all standalone emails (not in threads)
        foreach (var mailItem in _sourceItems.Where(e => !e.IsDisplayedInThread))
        {
            mailItem.IsSelected = false;
        }

        // Deselect all thread expanders and individual emails in threads
        foreach (var expander in _threadExpanders.Values)
        {
            expander.IsSelected = false;

            // Also explicitly deselect individual emails within threads
            foreach (var threadEmail in expander.ThreadEmails)
            {
                threadEmail.IsSelected = false;
            }
        }

        SelectedVisibleCount = _selectedVisibleItems.Count;

        var finalSelectedCount = SelectedItems.Count();
        var deselectedCount = initialSelectedCount - finalSelectedCount;

        if (deselectedCount > 0)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        return deselectedCount;
    }

    private void AddThreadToUI(ThreadMailItemViewModel expander, string groupKey)
    {
        var groupHeader = GetOrCreateGroupHeader(groupKey);
        var headerIndex = _groupHeaderIndexCache.GetValueOrDefault(groupKey, -1);

        if (headerIndex == -1)
        {
            // New group - add header, expander, and thread emails (if expanded)
            var insertPosition = FindGroupInsertionPosition(groupKey);

            Items.Insert(insertPosition, groupHeader);
            Items.Insert(insertPosition + 1, expander);

            var currentIndex = insertPosition + 2;
            var totalInserted = 2 // header + expander
                ;

            // Only add thread emails if the thread is expanded
            if (expander.IsThreadExpanded)
            {
                var sortedThreadEmails = SortDirection == EmailSortDirection.Descending
                    ? expander.ThreadEmails.OrderByDescending(e => e.MailCopy?.CreationDate).ToList()
                    : expander.ThreadEmails.OrderBy(e => e.MailCopy?.CreationDate).ToList();

                foreach (var email in sortedThreadEmails)
                {
                    Items.Insert(currentIndex, email);
                    RegisterMailItemForSelectionTracking(email);
                    currentIndex++;
                    totalInserted++;
                }
            }

            UpdateHeaderIndicesAfterInsertion(insertPosition, totalInserted);
            _groupHeaderIndexCache[groupKey] = insertPosition;
        }
        else
        {
            // Existing group - find correct position within group
            var groupEndIndex = FindGroupEndIndex(headerIndex);
            var insertIndex = FindItemInsertionIndexInGroup(expander, headerIndex, groupEndIndex);

            // Insert expander
            Items.Insert(insertIndex, expander);
            var currentIndex = insertIndex + 1;
            var totalInserted = 1 // expander
                ;

            // Only insert thread emails if expanded
            if (expander.IsThreadExpanded)
            {
                var sortedThreadEmails = SortDirection == EmailSortDirection.Descending
                    ? expander.ThreadEmails.OrderByDescending(e => e.MailCopy?.CreationDate).ToList()
                    : expander.ThreadEmails.OrderBy(e => e.MailCopy?.CreationDate).ToList();

                foreach (var email in sortedThreadEmails)
                {
                    Items.Insert(currentIndex, email);
                    RegisterMailItemForSelectionTracking(email);
                    currentIndex++;
                    totalInserted++;
                }
            }

            UpdateHeaderIndicesAfterInsertion(insertIndex, totalInserted);
        }

        UpdateGroupHeaderCounts(groupKey, groupHeader);
    }

    private void RemoveThreadFromUI(ThreadMailItemViewModel expander)
    {
        // Remove expander
        var expanderIndex = Items.IndexOf(expander);
        if (expanderIndex >= 0)
        {
            Items.RemoveAt(expanderIndex);
            UpdateHeaderIndicesAfterRemoval(expanderIndex);
        }

        // Remove all thread emails (whether expanded or not)
        foreach (var email in expander.ThreadEmails.ToList())
        {
            var emailIndex = Items.IndexOf(email);
            if (emailIndex >= 0)
            {
                UnregisterMailItemFromSelectionTracking(email);
                Items.RemoveAt(emailIndex);
                UpdateHeaderIndicesAfterRemoval(emailIndex);
            }
        }
    }

    private void RemoveEmailFromUI(MailItemViewModel email)
    {
        var itemIndex = Items.IndexOf(email);
        if (itemIndex >= 0)
        {
            UnregisterMailItemFromSelectionTracking(email);
            Items.RemoveAt(itemIndex);
            UpdateHeaderIndicesAfterRemoval(itemIndex);
        }
    }

    private void AddEmailToUI(MailItemViewModel email)
    {
        var groupKey = GetGroupKey(email);
        var groupHeader = GetOrCreateGroupHeader(groupKey);
        var headerIndex = _groupHeaderIndexCache.GetValueOrDefault(groupKey, -1);

        if (headerIndex == -1)
        {
            // New group
            var insertPosition = FindGroupInsertionPosition(groupKey);
            Items.Insert(insertPosition, groupHeader);
            Items.Insert(insertPosition + 1, email);
            RegisterMailItemForSelectionTracking(email);

            UpdateHeaderIndicesAfterInsertion(insertPosition, 2);
            _groupHeaderIndexCache[groupKey] = insertPosition;
        }
        else
        {
            // Existing group
            var groupEndIndex = FindGroupEndIndex(headerIndex);
            var insertIndex = FindItemInsertionIndexInGroup(email, headerIndex, groupEndIndex);
            Items.Insert(insertIndex, email);
            RegisterMailItemForSelectionTracking(email);

            UpdateHeaderIndicesAfterInsertion(insertIndex);
        }

        UpdateGroupHeaderCounts(groupKey, groupHeader);
    }

    #region Helper Methods

    private string GetGroupKeyForItem(object item)
    {
        return item switch
        {
            MailItemViewModel email => GetGroupKey(email),
            ThreadMailItemViewModel expander => GetGroupKey(expander.ThreadEmails.OrderByDescending(e => e.MailCopy?.CreationDate).First()),
            _ => "Default"
        };
    }

    private string GetGroupKey(MailItemViewModel email)
    {
        return GroupingType switch
        {
            EmailGroupingType.ByFromName => email.FromName ?? "Unknown Sender",
            EmailGroupingType.ByDate => email.MailCopy?.CreationDate.ToString("yyyy-MM-dd") ?? DateTime.Today.ToString("yyyy-MM-dd"),
            _ => "Default"
        };
    }

    private DateTime GetEffectiveDate(object item)
    {
        return item switch
        {
            MailItemViewModel email => email.MailCopy?.CreationDate ?? DateTime.MinValue,
            ThreadMailItemViewModel expander => expander.LatestMailViewModel?.CreationDate ?? DateTime.MinValue,
            _ => DateTime.MinValue
        };
    }

    private int FindInsertionIndex(MailItemViewModel email)
    {
        var createdAt = email.MailCopy!.CreationDate;
        int left = 0, right = _sourceItems.Count;

        while (left < right)
        {
            int mid = (left + right) / 2;
            var comparison = createdAt.CompareTo(_sourceItems[mid].MailCopy?.CreationDate ?? DateTime.MinValue);

            if (SortDirection == EmailSortDirection.Descending)
                comparison = -comparison;

            if (comparison < 0)
                right = mid;
            else
                left = mid + 1;
        }

        return left;
    }

    private GroupHeaderBase GetOrCreateGroupHeader(string groupKey)
    {
        if (!_groupHeaders.TryGetValue(groupKey, out var groupHeader))
        {
            groupHeader = CreateGroupHeader(groupKey);
            _groupHeaders[groupKey] = groupHeader;
            _groupItems[groupKey] = [];
        }
        return groupHeader;
    }

    private GroupHeaderBase CreateGroupHeader(string groupKey)
    {
        return GroupingType switch
        {
            EmailGroupingType.ByFromName => new SenderGroupHeader(groupKey),
            EmailGroupingType.ByDate when DateTime.TryParse(groupKey, out var date) => new DateGroupHeader(date),
            EmailGroupingType.ByDate => new DateGroupHeader(DateTime.Today),
            _ => new SenderGroupHeader(groupKey)
        };
    }

    private IComparer<string> GetGroupComparer()
    {
        return GroupingType switch
        {
            EmailGroupingType.ByFromName => SortDirection == EmailSortDirection.Descending
                ? StringComparer.OrdinalIgnoreCase.Reverse()
                : StringComparer.OrdinalIgnoreCase,
            EmailGroupingType.ByDate => SortDirection == EmailSortDirection.Descending
                ? CreateDateComparer(descending: true)
                : CreateDateComparer(descending: false),
            _ => StringComparer.Ordinal
        };
    }

    private static IComparer<string> CreateDateComparer(bool descending)
    {
        return Comparer<string>.Create((x, y) =>
        {
            var dateX = DateTime.TryParse(x, out var dx) ? dx : DateTime.MinValue;
            var dateY = DateTime.TryParse(y, out var dy) ? dy : DateTime.MinValue;

            var result = dateX.CompareTo(dateY);
            return descending ? -result : result;
        });
    }

    private int FindGroupInsertionPosition(string groupKey)
    {
        var comparer = GetGroupComparer();

        if (_groupHeaderIndexCache.Count == 0)
            return 0;

        var sortedGroups = _groupHeaderIndexCache.Keys.OrderBy(k => k, comparer).ToList();
        var insertPosition = 0;

        for (int i = 0; i < sortedGroups.Count; i++)
        {
            var existingGroupKey = sortedGroups[i];
            var comparison = comparer.Compare(groupKey, existingGroupKey);

            if (comparison < 0)
            {
                insertPosition = _groupHeaderIndexCache[existingGroupKey];
                break;
            }
            else if (i == sortedGroups.Count - 1)
            {
                var lastGroupHeaderIndex = _groupHeaderIndexCache[existingGroupKey];
                var lastGroupItemCount = _groupItems[existingGroupKey].Count;
                insertPosition = lastGroupHeaderIndex + 1 + lastGroupItemCount;
            }
        }

        return insertPosition;
    }

    private int FindGroupEndIndex(int headerIndex)
    {
        var groupKey = string.Empty;
        foreach (var kvp in _groupHeaderIndexCache)
        {
            if (kvp.Value == headerIndex)
            {
                groupKey = kvp.Key;
                break;
            }
        }

        return headerIndex + 1 + _groupItems.GetValueOrDefault(groupKey, []).Count;
    }

    private int FindItemInsertionIndexInGroup(object item, int groupStartIndex, int groupEndIndex)
    {
        var itemDate = GetEffectiveDate(item);

        for (int i = groupStartIndex + 1; i < groupEndIndex; i++)
        {
            var existingItem = Items[i];
            var existingDate = GetEffectiveDate(existingItem);

            var comparison = itemDate.CompareTo(existingDate);
            if (SortDirection == EmailSortDirection.Descending)
                comparison = -comparison;

            if (comparison < 0)
                return i;
        }

        return groupEndIndex;
    }

    private void UpdateHeaderIndicesAfterInsertion(int insertIndex, int itemCount = 1)
    {
        var keysToUpdate = _groupHeaderIndexCache
            .Where(kvp => kvp.Value > insertIndex)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToUpdate)
        {
            _groupHeaderIndexCache[key] += itemCount;
        }
    }

    private void UpdateHeaderIndicesAfterRemoval(int removeIndex)
    {
        var keysToUpdate = _groupHeaderIndexCache
            .Where(kvp => kvp.Value > removeIndex)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToUpdate)
        {
            _groupHeaderIndexCache[key]--;
        }
    }

    private void UpdateAllGroupHeaderCounts()
    {
        foreach (var (groupKey, groupHeader) in _groupHeaders)
        {
            UpdateGroupHeaderCounts(groupKey, groupHeader);
        }
    }

    private void UpdateGroupHeaderCounts(string groupKey, GroupHeaderBase groupHeader)
    {
        var emailsInGroup = _sourceItems.Where(e => GetGroupKey(e) == groupKey).ToList();
        var expandersInGroup = _threadExpanders.Values
            .Where(exp => GetGroupKeyForItem(exp) == groupKey)
            .ToList();

        var totalEmailCount = emailsInGroup.Count;
        var unreadCount = emailsInGroup.Count(e => e.MailCopy?.IsRead == false);

        groupHeader.ItemCount = totalEmailCount;
        groupHeader.UnreadCount = unreadCount;
    }

    private void UpdateGroupAfterChanges()
    {
        // Update all group header counts and remove empty groups
        var groupsToRemove = new List<string>();

        foreach (var (groupKey, groupHeader) in _groupHeaders.ToList())
        {
            UpdateGroupHeaderCounts(groupKey, groupHeader);

            if (groupHeader.ItemCount == 0)
            {
                groupsToRemove.Add(groupKey);
            }
        }

        foreach (var groupKey in groupsToRemove)
        {
            RemoveGroupHeader(groupKey);
        }
    }

    private void RemoveGroupHeader(string groupKey)
    {
        if (_groupHeaderIndexCache.TryGetValue(groupKey, out var headerIndex))
        {
            Items.RemoveAt(headerIndex);
            UpdateHeaderIndicesAfterRemoval(headerIndex);

            _groupHeaderIndexCache.Remove(groupKey);
            _groupHeaders.Remove(groupKey);
            _groupItems.Remove(groupKey);
        }
    }

    #region Incremental Refresh Helper Methods

    /// <summary>
    /// Updates thread expanders when new emails are added
    /// </summary>
    private void UpdateThreadExpandersForNewEmails(IList<MailItemViewModel> newEmails)
    {
        // Group new emails by ThreadId
        var newThreadGroups = newEmails
            .Where(e => !string.IsNullOrEmpty(e.MailCopy?.ThreadId))
            .GroupBy(e => e.MailCopy!.ThreadId!)
            .ToList();

        foreach (var threadGroup in newThreadGroups)
        {
            var threadId = threadGroup.Key;

            if (_threadExpanders.TryGetValue(threadId, out var existingExpander))
            {
                // Add new emails to existing thread
                foreach (var email in threadGroup)
                {
                    existingExpander.AddEmail(email);
                    email.IsDisplayedInThread = true;
                }
            }
            else
            {
                // Check if we need to create a new thread with existing emails
                var existingEmailsInThread = _sourceItems
                    .Where(e => e.MailCopy?.ThreadId == threadId && !threadGroup.Contains(e))
                    .ToList();

                var allThreadEmails = existingEmailsInThread.Concat(threadGroup).ToList();

                if (allThreadEmails.Count >= 2)
                {
                    // Create new thread expander
                    var expander = new ThreadMailItemViewModel(threadId);
                    _threadExpanders[threadId] = expander;

                    // Add all emails to the thread
                    foreach (var email in allThreadEmails)
                    {
                        expander.AddEmail(email);
                        email.IsDisplayedInThread = true;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Finds the correct position to insert an email in the UI Items collection
    /// </summary>
    private int FindUIInsertionPosition(MailItemViewModel email, string groupKey)
    {
        // If group doesn't exist yet, find position for new group
        if (!_groupHeaderIndexCache.ContainsKey(groupKey))
        {
            return FindGroupInsertionPosition(groupKey);
        }

        var headerIndex = _groupHeaderIndexCache[groupKey];
        var groupEndIndex = FindGroupEndIndex(headerIndex);

        return FindItemInsertionIndexInGroup(email, headerIndex, groupEndIndex);
    }

    /// <summary>
    /// Finds the correct position to insert an email within a group's items list
    /// </summary>
    private int FindGroupInsertionIndex(MailItemViewModel email, List<object> groupItems)
    {
        var emailDate = email.MailCopy?.CreationDate ?? DateTime.MinValue;

        for (int i = 0; i < groupItems.Count; i++)
        {
            var existingDate = GetEffectiveDate(groupItems[i]);
            var comparison = emailDate.CompareTo(existingDate);

            if (SortDirection == EmailSortDirection.Descending)
                comparison = -comparison;

            if (comparison < 0)
                return i;
        }

        return groupItems.Count;
    }

    /// <summary>
    /// Finds the correct position to insert a group header
    /// </summary>
    private int FindHeaderInsertionPosition(string groupKey, GroupHeaderBase groupHeader)
    {
        if (_groupHeaderIndexCache.Count == 0)
            return 0;

        var comparer = GetGroupComparer();
        var insertPosition = 0;

        foreach (var kvp in _groupHeaderIndexCache.OrderBy(k => k.Key, comparer))
        {
            var existingGroupKey = kvp.Key;
            var comparison = comparer.Compare(groupKey, existingGroupKey);

            if (comparison < 0)
            {
                insertPosition = kvp.Value;
                break;
            }
            else
            {
                var groupEndIndex = FindGroupEndIndex(kvp.Value);
                insertPosition = groupEndIndex;
            }
        }

        return insertPosition;
    }

    /// <summary>
    /// Updates header indices after a new group is inserted
    /// </summary>
    private void UpdateSubsequentHeaderIndices(string insertedGroupKey, int itemCount)
    {
        var insertedHeaderIndex = _groupHeaderIndexCache[insertedGroupKey];

        var keysToUpdate = _groupHeaderIndexCache
            .Where(kvp => kvp.Key != insertedGroupKey && kvp.Value > insertedHeaderIndex)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in keysToUpdate)
        {
            _groupHeaderIndexCache[key] += itemCount;
        }
    }

    /// <summary>
    /// Updates group header counts for affected groups when new emails are added
    /// </summary>
    private void UpdateGroupHeaderCountsForNewEmails(IList<MailItemViewModel> newEmails)
    {
        var affectedGroups = newEmails
            .Select(email => GetGroupKey(email))
            .Distinct()
            .ToList();

        foreach (var groupKey in affectedGroups)
        {
            if (_groupHeaders.TryGetValue(groupKey, out var groupHeader))
            {
                UpdateGroupHeaderCounts(groupKey, groupHeader);
            }
        }
    }

    #endregion

    private void OnSourceItemsChanged(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_isUpdating)
        {
            RefreshGrouping();
        }
    }

    #endregion

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            _sourceItems.CollectionChanged -= OnSourceItemsChanged;

            // Unregister all mail items from selection tracking
            foreach (var item in Items)
            {
                if (item is MailItemViewModel mailItem)
                {
                    UnregisterMailItemFromSelectionTracking(mailItem);
                }
            }

            // Unregister from messenger
            WeakReferenceMessenger.Default.Unregister<PropertyChangedMessage<bool>>(this);

            // Reset IsDisplayedInThread for all emails before disposal
            foreach (var email in _sourceItems)
            {
                email.IsDisplayedInThread = false;
            }

            // Dispose all thread expanders
            foreach (var expander in _threadExpanders.Values)
            {
                expander.Dispose();
            }

            _sourceItems.Clear();
            Items.Clear();
            _groupHeaders.Clear();
            _groupHeaderIndexCache.Clear();
            _groupItems.Clear();
            _threadExpanders.Clear();
            _selectedVisibleItems.Clear();
            SelectedVisibleCount = 0;
        }

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Extension method to reverse IComparer for descending sorts
/// </summary>
internal static class ComparerExtensions
{
    public static IComparer<T> Reverse<T>(this IComparer<T> comparer)
    {
        return Comparer<T>.Create((x, y) => comparer.Compare(y, x));
    }
}
