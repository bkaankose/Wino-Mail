using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
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
    private readonly ObservableCollection<MailItemViewModel> _sourceItems = [];
    private readonly Dictionary<string, GroupHeaderBase> _groupHeaders = [];
    private readonly Dictionary<string, int> _groupHeaderIndexCache = [];
    private readonly Dictionary<string, List<object>> _groupItems = [];
    private readonly Dictionary<string, ThreadMailItemViewModel> _threadExpanders = [];
    private bool _disposed;
    private bool _isUpdating;

    [ObservableProperty]
    private EmailGroupingType groupingType = EmailGroupingType.ByDate;

    [ObservableProperty]
    private EmailSortDirection sortDirection = EmailSortDirection.Descending;

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
    /// Gets all email items across all groups as a flat collection
    /// </summary>
    public IEnumerable<MailItemViewModel> AllItems => _sourceItems;

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
    /// Handles PropertyChanged messages for thread expansion
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
                        var sourceItem = _sourceItems.FirstOrDefault(a => a == email);

                        Items.RemoveAt(emailIndex);
                        UpdateHeaderIndicesAfterRemoval(emailIndex);
                    }
                }
            }
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

        _isUpdating = true;
        try
        {
            var threadId = email.MailCopy.ThreadId;

            // Remove from source collection
            if (!_sourceItems.Remove(email))
                return; // Email not found

            if (!string.IsNullOrEmpty(threadId) && _threadExpanders.TryGetValue(threadId, out var expander))
            {
                // Remove from thread
                expander.RemoveEmail(email);
                email.IsDisplayedInThread = false;

                // Remove email from UI
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
            // For bulk loading, add to source and refresh
            foreach (var email in emailList)
            {
                var insertIndex = FindInsertionIndex(email);
                _sourceItems.Insert(insertIndex, email);
            }

            RefreshGrouping();

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

            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(TotalUnreadCount));
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
            // Clear UI items but preserve source and expanders
            Items.Clear();
            _groupHeaders.Clear();
            _groupHeaderIndexCache.Clear();
            _groupItems.Clear();

            if (!_sourceItems.Any())
                return;

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
                                currentIndex++;
                            }
                        }
                    }
                    else if (item is MailItemViewModel email)
                    {
                        // Add standalone email
                        Items.Add(email);
                        currentIndex++;
                    }
                }
            }

            // Update group header counts
            UpdateAllGroupHeaderCounts();
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

    private void RefreshThreadInUI(ThreadMailItemViewModel expander)
    {
        // Remove thread completely from UI
        RemoveThreadFromUI(expander);

        // Find correct position for thread expander based on latest email
        var groupKey = GetGroupKeyForItem(expander);
        AddThreadToUI(expander, groupKey);
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

            UpdateHeaderIndicesAfterInsertion(insertPosition, 2);
            _groupHeaderIndexCache[groupKey] = insertPosition;
        }
        else
        {
            // Existing group
            var groupEndIndex = FindGroupEndIndex(headerIndex);
            var insertIndex = FindItemInsertionIndexInGroup(email, headerIndex, groupEndIndex);
            Items.Insert(insertIndex, email);

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
            ThreadMailItemViewModel expander => expander.LatestEmailDate,
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

    private void OnSourceItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
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
