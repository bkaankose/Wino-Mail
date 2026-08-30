using System.Collections.ObjectModel;
using Microsoft.UI.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Core;
using Wino.Mail.Controls.Core;
using VirtualKey = Windows.System.VirtualKey;

namespace Wino.Mail.Controls.MailListView;

/// <summary>
/// A virtualized, grouped mail list that projects a flat source into thread rows
/// while keeping selection expressed as stable leaf-mail identities.
/// </summary>
public partial class WinoMailListView : ListView
{
    private readonly ObservableCollection<IMailListSourceItem> _selectedItems = [];
    private readonly ObservableCollection<string> _selectedThreadKeys = [];
    private readonly ObservableCollection<string> _expandedThreadKeys = [];
    private CollectionViewSource? _fallbackViewSource;
    private readonly MailListRowTemplateSelector _templateSelector = new();
    private readonly HashSet<SelectionToken> _tokens = [];
    private MailListProjection? _projection;
    private bool _isProjectionChanging;
    private bool _isRestoringSelection;
    private bool _isSelectionRestoreQueued;
    private bool _restoreSelectionSynchronouslyAfterProjectionChange;
    private bool _isTemplateApplied;
    private bool _isReattachingItemsSource;
    private MailListLoadTrace? _pendingFrameTrace;
    private int _lastLoadMoreCount = -1;
    private TaskCompletionSource<bool>? _selectionRestoreCompletion;
    private MailListRow? _pressedRow;
    private bool _pressedRowWasSelected;
    private IMailListSourceItem? _multiSelectRetainedItem;
    private IMailListCollection? _mailItemsSource;
    private MailListProjectionOptions? _projectionOptions;

    public WinoMailListView()
    {
        SelectedMailItems = new ReadOnlyObservableCollection<IMailListSourceItem>(_selectedItems);
        SelectedThreadKeys = new ReadOnlyObservableCollection<string>(_selectedThreadKeys);
        ExpandedThreadKeys = new ReadOnlyObservableCollection<string>(_expandedThreadKeys);

        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        IsItemClickEnabled = true;
        IsMultiSelectCheckBoxEnabled = true;
        SelectionMode = ListViewSelectionMode.Extended;
        SelectionChanged += OnNativeSelectionChanged;
        ItemClick += OnItemClick;
        ContainerContentChanging += OnContainerContentChanging;
    }

    public event EventHandler<MailListSelectionSnapshot>? SelectionSnapshotChanged;

    public event EventHandler<ThreadExpansionChangedEventArgs>? ThreadExpansionChanged;

    public event EventHandler? LoadMoreRequested;

    public static readonly DependencyProperty SingleItemTemplateProperty = DependencyProperty.Register(
        nameof(SingleItemTemplate),
        typeof(DataTemplate),
        typeof(WinoMailListView),
        new PropertyMetadata(null, OnTemplateChanged));

    public static readonly DependencyProperty ThreadHeaderTemplateProperty = DependencyProperty.Register(
        nameof(ThreadHeaderTemplate),
        typeof(DataTemplate),
        typeof(WinoMailListView),
        new PropertyMetadata(null, OnTemplateChanged));

    public static readonly DependencyProperty ThreadChildTemplateProperty = DependencyProperty.Register(
        nameof(ThreadChildTemplate),
        typeof(DataTemplate),
        typeof(WinoMailListView),
        new PropertyMetadata(null, OnTemplateChanged));

    public static readonly DependencyProperty GroupHeaderTemplateProperty = DependencyProperty.Register(
        nameof(GroupHeaderTemplate),
        typeof(DataTemplate),
        typeof(WinoMailListView),
        new PropertyMetadata(null, OnGroupHeaderTemplateChanged));

    public static readonly DependencyProperty IsTouchMultiSelectModeProperty = DependencyProperty.Register(
        nameof(IsTouchMultiSelectMode),
        typeof(bool),
        typeof(WinoMailListView),
        new PropertyMetadata(false, OnIsTouchMultiSelectModeChanged));

    public IMailListCollection? MailItemsSource
    {
        get => _mailItemsSource;
        set
        {
            if (ReferenceEquals(_mailItemsSource, value))
            {
                return;
            }

            _mailItemsSource = value;
            if (_isTemplateApplied)
            {
                AttachProjection();
            }
        }
    }

    public MailListProjectionOptions? ProjectionOptions
    {
        get => _projectionOptions;
        set
        {
            if (_projectionOptions == value)
            {
                return;
            }

            _projectionOptions = value;
            _projection?.SetOptions(value ?? new());
        }
    }

    public DataTemplate? SingleItemTemplate
    {
        get => (DataTemplate?)GetValue(SingleItemTemplateProperty);
        set => SetValue(SingleItemTemplateProperty, value);
    }

    public DataTemplate? ThreadHeaderTemplate
    {
        get => (DataTemplate?)GetValue(ThreadHeaderTemplateProperty);
        set => SetValue(ThreadHeaderTemplateProperty, value);
    }

    public DataTemplate? ThreadChildTemplate
    {
        get => (DataTemplate?)GetValue(ThreadChildTemplateProperty);
        set => SetValue(ThreadChildTemplateProperty, value);
    }

    public DataTemplate? GroupHeaderTemplate
    {
        get => (DataTemplate?)GetValue(GroupHeaderTemplateProperty);
        set => SetValue(GroupHeaderTemplateProperty, value);
    }

    public bool IsTouchMultiSelectMode
    {
        get => (bool)GetValue(IsTouchMultiSelectModeProperty);
        set => SetValue(IsTouchMultiSelectModeProperty, value);
    }

    public ReadOnlyObservableCollection<IMailListSourceItem> SelectedMailItems { get; }

    /// <summary>
    /// Optional XAML-created grouped view source. Native AOT requires the page's
    /// generated XAML code to root CollectionViewSource.View and its grouped ABI.
    /// </summary>
    public CollectionViewSource? GroupedViewSource { get; set; }

    public ReadOnlyObservableCollection<string> SelectedThreadKeys { get; }

    public ReadOnlyObservableCollection<string> ExpandedThreadKeys { get; }

    public MailListSelectionSnapshot SelectionSnapshot { get; private set; } =
        MailListSelectionSnapshot.Empty;

    public bool IsThreadExpanded(string threadKey) =>
        _projection?.IsThreadExpanded(threadKey) == true;

    public IMailListSourceItem? GetAdjacentVisibleItem(Guid stableId, int offset = 1) =>
        _projection?.GetAdjacentVisibleItem(stableId, offset);

    public void ExpandThread(string threadKey)
    {
        if (_projection?.FindThread(threadKey) is not { Count: > 1 } ||
            _projection.IsThreadExpanded(threadKey))
        {
            return;
        }

        _projection.ExpandThread(threadKey);
    }

    public void CollapseThread(string threadKey)
    {
        if (_projection?.IsThreadExpanded(threadKey) == true)
        {
            _projection.CollapseThread(threadKey);
        }
    }

    public async Task<bool> SelectItemAsync(Guid stableId, bool scrollIntoView = true)
    {
        if (_projection?.FindItem(stableId) is not { } item)
        {
            return false;
        }

        _tokens.Clear();
        _tokens.Add(SelectionToken.ForItem(item));
        if (_projection.GetThreadForItem(stableId) is { } thread &&
            !_projection.IsThreadExpanded(thread.Key))
        {
            _projection.ExpandThread(thread.Key);
        }
        else
        {
            QueueSelectionRestore();
        }

        await WaitForSelectionRestoreAsync();
        if (scrollIntoView && _projection.FindRow(stableId) is { } row)
        {
            ScrollIntoView(row);
        }

        return true;
    }

    public Task<bool> SelectMailAsync(Guid stableId, bool scrollIntoView = true) =>
        SelectItemAsync(stableId, scrollIntoView);

    public void ClearSelection()
    {
        _tokens.Clear();
        QueueSelectionRestore();
    }

    public void KeepNewestSelection()
    {
        var newest = _selectedItems.MaxBy(static item => item.DateSortKey);
        if (newest is null)
        {
            ClearSelection();
            return;
        }

        _tokens.Clear();
        _tokens.Add(SelectionToken.ForItem(newest));
        QueueSelectionRestore();
    }

    private void KeepActiveSelection()
    {
        var activeItem = _multiSelectRetainedItem ??
                         SelectionSnapshot.ActiveItem ??
                         _selectedItems.MaxBy(static item => item.DateSortKey);
        _multiSelectRetainedItem = null;
        if (activeItem is null)
        {
            ClearSelection();
            return;
        }

        _tokens.Clear();
        _tokens.Add(SelectionToken.ForItem(activeItem));
        QueueSelectionRestore();
    }

    public Task WaitForSelectionSyncAsync() => WaitForSelectionRestoreAsync();

    public void ExpandThreadFromExpander(string threadKey) => ExpandThread(threadKey);

    public void CollapseThreadFromExpander(string threadKey) => CollapseThread(threadKey);

    public virtual void Cleanup()
    {
        DetachProjection();
    }

    public void SetSelectedItems(IEnumerable<Guid> stableIds)
    {
        ArgumentNullException.ThrowIfNull(stableIds);
        _tokens.Clear();
        if (_projection is not null)
        {
            foreach (var id in stableIds.Distinct())
            {
                if (_projection.FindItem(id) is { } item)
                {
                    _tokens.Add(SelectionToken.ForItem(item));
                }
            }
        }

        QueueSelectionRestore();
    }

    public new void SelectAll()
    {
        _tokens.Clear();
        if (_projection is not null)
        {
            foreach (var item in _projection.Items)
            {
                _tokens.Add(SelectionToken.ForItem(item));
            }
        }

        QueueSelectionRestore();
    }

    public new void DeselectRange(ItemIndexRange itemIndexRange)
    {
        base.DeselectRange(itemIndexRange);
        CaptureNativeSelection();
    }

    protected override DependencyObject GetContainerForItemOverride() => new WinoMailListViewItem();

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _isTemplateApplied = true;
        ApplyTemplates();
        ApplyGroupHeaderTemplate();
        AttachProjection();
    }

    protected override bool IsItemItsOwnContainerOverride(object item) => item is WinoMailListViewItem;

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is WinoMailListViewItem container && item is MailListRow row)
        {
            container.OwnerList = this;
            container.Row = row;
            container.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        }
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is WinoMailListViewItem container)
        {
            container.OwnerList = null;
            container.Row = null;
        }

        base.ClearContainerForItemOverride(element, item);
    }

    internal void RecordPointerPressed(MailListRow? row, bool isSelected)
    {
        _pressedRow = row;
        _pressedRowWasSelected = isSelected;
    }

    private static void OnTemplateChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        ((WinoMailListView)sender).ApplyTemplates();
    }

    private static void OnGroupHeaderTemplateChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        ((WinoMailListView)sender).ApplyGroupHeaderTemplate();
    }

    private void AttachProjection()
    {
        DetachProjection();
        if (MailItemsSource is null)
        {
            ItemsSource = null;
            PublishSelectionSnapshot();
            return;
        }

        _projection = new MailListProjection(
            MailItemsSource,
            ProjectionOptions ?? new MailListProjectionOptions());
        _projection.ProjectionChanging += OnProjectionChanging;
        _projection.ProjectionChanged += OnProjectionChanged;
        _projection.ThreadExpansionChanged += OnThreadExpansionChanged;
        _projection.GroupsResetting += OnProjectionGroupsResetting;
        _projection.GroupsReset += OnProjectionGroupsReset;
        var viewSource = GetViewSource();
        viewSource.Source = _projection.Groups;
        ItemsSource = viewSource.View;

        SynchronizeExpandedThreadKeys();
        QueueSelectionRestore();
    }

    private void DetachProjection()
    {
        if (_projection is null)
        {
            return;
        }

        _projection.ProjectionChanging -= OnProjectionChanging;
        _projection.ProjectionChanged -= OnProjectionChanged;
        _projection.ThreadExpansionChanged -= OnThreadExpansionChanged;
        _projection.GroupsResetting -= OnProjectionGroupsResetting;
        _projection.GroupsReset -= OnProjectionGroupsReset;
        _projection.Dispose();
        _projection = null;
        GetViewSource().Source = null;
        ItemsSource = null;
    }

    /// <summary>
    /// Detaches the items source for the duration of a wholesale group replacement so the
    /// list performs one refresh instead of reacting to every group being added back.
    /// </summary>
    private void OnProjectionGroupsResetting(object? sender, EventArgs args)
    {
        if (ItemsSource is null)
        {
            return;
        }

        _isReattachingItemsSource = true;
        ItemsSource = null;
    }

    private void OnProjectionGroupsReset(object? sender, EventArgs args)
    {
        _lastLoadMoreCount = -1;
        if (!_isReattachingItemsSource)
        {
            return;
        }

        _isReattachingItemsSource = false;
        ItemsSource = GetViewSource().View;
    }

    /// <summary>
    /// Records the first composition frame after a new page is published, which is the point
    /// the user actually sees rows. Detaches itself after a single frame.
    /// </summary>
    private void QueueFirstFrameMark()
    {
        if (MailListLoadTrace.Current is not { } trace || _pendingFrameTrace is not null)
        {
            return;
        }

        // The trace instance is captured now: by the time the frame renders a newer load
        // may already own MailListLoadTrace.Current.
        _pendingFrameTrace = trace;
        CompositionTarget.Rendering += OnFirstFrameRendered;
    }

    private void OnFirstFrameRendered(object? sender, object args)
    {
        CompositionTarget.Rendering -= OnFirstFrameRendered;
        var trace = _pendingFrameTrace;
        _pendingFrameTrace = null;
        trace?.Mark(MailListLoadStage.FirstFrameRendered);
    }

    private CollectionViewSource GetViewSource()
    {
        if (GroupedViewSource is not null)
        {
            return GroupedViewSource;
        }

        _fallbackViewSource ??= new CollectionViewSource
        {
            IsSourceGrouped = true,
        };
        return _fallbackViewSource;
    }

    private void OnProjectionChanging(object? sender, EventArgs args)
    {
        // Native selection is cleared while row instances are replaced. Tokens are
        // identity-based and must survive until they can be restored to the new rows.
        _isProjectionChanging = true;
    }

    private void OnProjectionChanged(object? sender, EventArgs args)
    {
        _isProjectionChanging = false;
        if (_restoreSelectionSynchronouslyAfterProjectionChange)
        {
            _restoreSelectionSynchronouslyAfterProjectionChange = false;
            RestoreSelection();
        }
        else
        {
            QueueSelectionRestore();
        }

        SynchronizeExpandedThreadKeys();
        QueueFirstFrameMark();
    }

    private void OnThreadExpansionChanged(object? sender, ThreadExpansionChangedEventArgs args)
    {
        SynchronizeExpandedThreadKeys();
        ThreadExpansionChanged?.Invoke(this, args);
    }

    private void OnNativeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_isProjectionChanging || _isRestoringSelection)
        {
            return;
        }

        if (SelectionMode == ListViewSelectionMode.Multiple)
        {
            if (_pressedRow?.IsThreadHead == true)
            {
                // Thread heads represent all leaves and are handled by ItemClick.
                return;
            }

            // Multiple mode already toggles individual leaf rows without Ctrl.
            CaptureNativeSelection();
            return;
        }

        var hasThreadHeadChange =
            args.AddedItems.OfType<MailListRow>().Any(static row => row.IsThreadHead) ||
            args.RemovedItems.OfType<MailListRow>().Any(static row => row.IsThreadHead);
        if (hasThreadHeadChange &&
            _pressedRow?.IsThreadHead == true &&
            !IsKeyDown(VirtualKey.Shift))
        {
            // Thread heads represent an interaction surface, not a separate mail.
            // ItemClick resolves normal, Ctrl, and touch gestures against stable
            // leaf tokens before a snapshot is published.
            return;
        }

        CaptureNativeSelection();
    }

    private void OnItemClick(object sender, ItemClickEventArgs args)
    {
        if (args.ClickedItem is not MailListRow row)
        {
            return;
        }

        var isControlGesture = IsKeyDown(VirtualKey.Control);
        var isShiftGesture = IsKeyDown(VirtualKey.Shift);
        var isMultiSelectGesture =
            SelectionMode == ListViewSelectionMode.Multiple ||
            isControlGesture;
        var wasPressedRowSelected =
            ReferenceEquals(_pressedRow, row) &&
            _pressedRowWasSelected;

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            try
            {
                if (_projection is null || isShiftGesture)
                {
                    return;
                }

                if (row.IsThreadHead && isMultiSelectGesture)
                {
                    ToggleWholeThreadSelection(row);
                    return;
                }

                if (!row.IsThreadHead)
                {
                    if (!isMultiSelectGesture &&
                        wasPressedRowSelected)
                    {
                        _tokens.Remove(SelectionToken.ForItem(row.SourceItem));
                        QueueSelectionRestore();
                    }

                    if (!isMultiSelectGesture)
                    {
                        CollapseExpandedThreadsExcept(row.IsThreadChild ? row.ThreadKey : null);
                    }

                    return;
                }

                if (_projection.IsThreadExpanded(row.ThreadKey))
                {
                    RemoveSelectionTokensForThread(row.ThreadKey);
                    _projection.CollapseThread(row.ThreadKey);
                }
                else
                {
                    CollapseExpandedThreadsExcept(row.ThreadKey);

                    // A normal thread click activates the ordered representative message.
                    // Its item token selects both the thread root and the matching first
                    // child after expansion, without treating every message as selected.
                    _tokens.Clear();
                    _tokens.Add(SelectionToken.ForItem(row.SourceItem));
                    RestoreSelection();
                    _restoreSelectionSynchronouslyAfterProjectionChange = true;
                    try
                    {
                        _projection.ExpandThread(row.ThreadKey);
                    }
                    finally
                    {
                        _restoreSelectionSynchronouslyAfterProjectionChange = false;
                    }
                }
            }
            finally
            {
                _pressedRow = null;
                _pressedRowWasSelected = false;
            }
        });
    }

    private void ToggleWholeThreadSelection(MailListRow row)
    {
        if (_projection is null || row.Thread is not { } thread)
        {
            return;
        }

        var threadToken = SelectionToken.ForThread(thread.Key);
        var isFullySelected = _tokens.Contains(threadToken) ||
            thread.Items.All(item => _tokens.Contains(SelectionToken.ForItem(item)));
        var shouldCollapse =
            isFullySelected &&
            _projection.IsThreadExpanded(thread.Key);

        RemoveSelectionTokensForThread(thread.Key);
        if (shouldCollapse)
        {
            _projection.CollapseThread(thread.Key);
            QueueSelectionRestore();
            return;
        }

        _tokens.Add(threadToken);
        if (!_projection.IsThreadExpanded(thread.Key))
        {
            _projection.ExpandThread(thread.Key, collapseOtherThreads: false);
        }

        QueueSelectionRestore();
    }

    private static void OnIsTouchMultiSelectModeChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (sender is not WinoMailListView list)
        {
            return;
        }

        var isEnabled = args.NewValue is true;
        if (isEnabled)
        {
            list._multiSelectRetainedItem =
                list.SelectionSnapshot.ActiveItem ??
                list._selectedItems.MaxBy(static item => item.DateSortKey);
        }

        list._isRestoringSelection = true;
        try
        {
            list.SelectionMode = isEnabled
                ? ListViewSelectionMode.Multiple
                : ListViewSelectionMode.Extended;
        }
        finally
        {
            list._isRestoringSelection = false;
        }

        if (isEnabled)
        {
            list.QueueSelectionRestore();
        }
        else if (args.OldValue is true)
        {
            list.KeepActiveSelection();
        }
    }

    private void CollapseExpandedThreadsExcept(string? retainedThreadKey)
    {
        if (_projection is null)
        {
            return;
        }

        var threadKeys = _projection.ExpandedThreadKeys
            .Where(key => !string.Equals(key, retainedThreadKey, StringComparison.Ordinal))
            .ToArray();

        foreach (var threadKey in threadKeys)
        {
            RemoveSelectionTokensForThread(threadKey);
            _projection.CollapseThread(threadKey);
        }
    }

    private void RemoveSelectionTokensForThread(string threadKey)
    {
        _tokens.RemoveWhere(token =>
            string.Equals(token.ThreadKey, threadKey, StringComparison.Ordinal));
    }

    private static bool IsKeyDown(VirtualKey key) =>
        InputKeyboardSource
            .GetKeyStateForCurrentThread(key)
            .HasFlag(CoreVirtualKeyStates.Down);

    private void CaptureNativeSelection()
    {
        if (_projection is null)
        {
            _tokens.Clear();
            PublishSelectionSnapshot();
            return;
        }

        _tokens.Clear();
        foreach (var row in SelectedItems.OfType<MailListRow>())
        {
            if (row.IsThreadHead && row.Thread is { } thread)
            {
                _tokens.Add(SelectionToken.ForThread(thread.Key));
            }
            else
            {
                _tokens.Add(SelectionToken.ForItem(row.SourceItem));
            }
        }

        PublishSelectionSnapshot();
    }

    private void QueueSelectionRestore()
    {
        if (!_isTemplateApplied)
        {
            PublishSelectionSnapshot();
            return;
        }

        if (_isSelectionRestoreQueued)
        {
            return;
        }

        _isSelectionRestoreQueued = true;
        _selectionRestoreCompletion ??=
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RestoreSelection))
        {
            RestoreSelection();
        }
    }

    private void RestoreSelection()
    {
        _isSelectionRestoreQueued = false;
        try
        {
            if (_projection is null)
            {
                PublishSelectionSnapshot();
                return;
            }

            _isRestoringSelection = true;
            try
            {
                var desiredRows = _projection.Rows
                    .Where(IsSelected)
                    .ToArray();
                var desiredSet = desiredRows.ToHashSet();

                foreach (var row in SelectedItems
                             .OfType<MailListRow>()
                             .Where(row => !desiredSet.Contains(row))
                             .ToArray())
                {
                    SelectedItems.Remove(row);
                }

                foreach (var row in desiredRows)
                {
                    if (!SelectedItems.Contains(row))
                    {
                        SelectedItems.Add(row);
                    }
                }
            }
            finally
            {
                _isRestoringSelection = false;
            }

            PublishSelectionSnapshot();
        }
        finally
        {
            var completion = _selectionRestoreCompletion;
            _selectionRestoreCompletion = null;
            completion?.TrySetResult(true);
        }
    }

    private bool IsSelected(MailListRow row)
    {
        if (_tokens.Contains(SelectionToken.ForThread(row.ThreadKey)))
        {
            return true;
        }

        return _tokens.Contains(SelectionToken.ForItem(row.SourceItem));
    }

    private void PublishSelectionSnapshot()
    {
        var selected = new List<IMailListSourceItem>();
        var selectedIds = new HashSet<Guid>();
        var fullySelectedThreads = new HashSet<string>(StringComparer.Ordinal);

        if (_projection is not null)
        {
            _tokens.RemoveWhere(token =>
                token.StableId is { } stableId
                    ? _projection.FindItem(stableId) is null
                    : token.ThreadKey is not null &&
                      _projection.FindThread(token.ThreadKey) is null);

            foreach (var token in _tokens)
            {
                if (token.ThreadKey is { } threadKey && token.StableId is null)
                {
                    if (_projection.FindThread(threadKey) is { } thread)
                    {
                        fullySelectedThreads.Add(threadKey);
                        foreach (var threadItem in thread.Items)
                        {
                            if (selectedIds.Add(threadItem.StableId))
                            {
                                selected.Add(threadItem);
                            }
                        }
                    }

                    continue;
                }

                if (token.StableId is { } id &&
                    _projection.FindItem(id) is { } item &&
                    selectedIds.Add(id))
                {
                    selected.Add(item);
                }
            }

            foreach (var thread in _projection.Threads)
            {
                if (thread.Items.All(item => selectedIds.Contains(item.StableId)))
                {
                    fullySelectedThreads.Add(thread.Key);
                }
            }
        }

        var activeItem = SelectedItems
            .OfType<MailListRow>()
            .LastOrDefault()
            ?.SourceItem;

        var selectedIdSet = selected.Select(static item => item.StableId).ToHashSet();
        var existingSelectedIdSet = SelectionSnapshot.SelectedItems
            .Select(static item => item.StableId)
            .ToHashSet();
        if (selectedIdSet.SetEquals(existingSelectedIdSet) &&
            fullySelectedThreads.SetEquals(SelectionSnapshot.FullySelectedThreadKeys) &&
            activeItem?.StableId == SelectionSnapshot.ActiveItem?.StableId)
        {
            return;
        }

        ReplaceContents(_selectedItems, selected);
        ReplaceContents(_selectedThreadKeys, fullySelectedThreads.Order(StringComparer.Ordinal));
        SelectionSnapshot = new(selected, fullySelectedThreads, activeItem);
        SelectionSnapshotChanged?.Invoke(this, SelectionSnapshot);
    }

    private void SynchronizeExpandedThreadKeys()
    {
        ReplaceContents(
            _expandedThreadKeys,
            _projection is null
                ? Enumerable.Empty<string>()
                : _projection.ExpandedThreadKeys.Order(StringComparer.Ordinal));
    }

    private void OnContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue)
        {
            MailListLoadTrace.MarkCurrent(MailListLoadStage.FirstContainerRealized);
        }

        if (_projection is null ||
            args.InRecycleQueue ||
            args.ItemIndex < Math.Max(0, _projection.RowCount - 1) ||
            _lastLoadMoreCount == MailItemsSource?.Count)
        {
            return;
        }

        _lastLoadMoreCount = MailItemsSource?.Count ?? 0;
        LoadMoreRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyTemplates()
    {
        if (SingleItemTemplate is null &&
            ThreadHeaderTemplate is null &&
            ThreadChildTemplate is null)
        {
            return;
        }

        _templateSelector.SingleItemTemplate = SingleItemTemplate;
        _templateSelector.ThreadHeaderTemplate = ThreadHeaderTemplate;
        _templateSelector.ThreadChildTemplate = ThreadChildTemplate;
        ItemTemplateSelector = null;
        ItemTemplateSelector = _templateSelector;
    }

    private void ApplyGroupHeaderTemplate()
    {
        if (GroupHeaderTemplate is null)
        {
            return;
        }

        GroupStyle.Clear();
        GroupStyle.Add(new GroupStyle { HeaderTemplate = GroupHeaderTemplate });
    }

    private Task WaitForSelectionRestoreAsync() =>
        _selectionRestoreCompletion?.Task ?? Task.CompletedTask;

    private static void ReplaceContents<T>(
        ObservableCollection<T> target,
        IEnumerable<T> values)
    {
        var replacements = values.ToArray();
        if (target.SequenceEqual(replacements))
        {
            return;
        }

        target.Clear();
        foreach (var value in replacements)
        {
            target.Add(value);
        }
    }

    private readonly record struct SelectionToken(string ThreadKey, Guid? StableId)
    {
        public static SelectionToken ForThread(string threadKey) => new(threadKey, null);

        public static SelectionToken ForItem(IMailListSourceItem item) =>
            new(item.ThreadKey ?? item.StableId.ToString("N"), item.StableId);
    }
}
