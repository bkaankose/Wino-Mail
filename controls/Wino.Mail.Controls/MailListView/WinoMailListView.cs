using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.WinUI;
using Microsoft.UI.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.Core;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.HoverActions;
using Wino.Mail.Controls.HoverActions;
using VirtualKey = Windows.System.VirtualKey;

namespace Wino.Mail.Controls.MailListView;

/// <summary>
/// A virtualized, grouped mail list that projects a flat source into thread rows
/// while keeping selection expressed as stable leaf-mail identities.
/// </summary>
public partial class WinoMailListView : ListView, IDisposable
{
    private const string ScrollViewerPartName = "ScrollViewer";
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
    private WinoMailListViewItem? _contextMenuContainer;
    private MailListRow? _pressedRow;
    private bool _pressedRowWasSelected;
    private IMailListSourceItem? _multiSelectRetainedItem;
    private IMailListCollection? _mailItemsSource;
    private MailListProjectionOptions? _projectionOptions;
    private RemovalAnchor _removalAnchor;
    private ViewportAnchor? _viewportAnchor;
    private ScrollViewer? _scrollViewer;
    private bool _focusWasInsideList;
    private bool _disposed;
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

    public static readonly DependencyProperty IsHoverActionsEnabledProperty = DependencyProperty.Register(
        nameof(IsHoverActionsEnabled),
        typeof(bool),
        typeof(WinoMailListView),
        new PropertyMetadata(true, OnHoverActionConfigurationChanged));

    public static readonly DependencyProperty LeftHoverActionProperty = DependencyProperty.Register(
        nameof(LeftHoverAction),
        typeof(HoverActionKind),
        typeof(WinoMailListView),
        new PropertyMetadata(HoverActionKind.None, OnHoverActionConfigurationChanged));

    public static readonly DependencyProperty CenterHoverActionProperty = DependencyProperty.Register(
        nameof(CenterHoverAction),
        typeof(HoverActionKind),
        typeof(WinoMailListView),
        new PropertyMetadata(HoverActionKind.None, OnHoverActionConfigurationChanged));

    public static readonly DependencyProperty RightHoverActionProperty = DependencyProperty.Register(
        nameof(RightHoverAction),
        typeof(HoverActionKind),
        typeof(WinoMailListView),
        new PropertyMetadata(HoverActionKind.None, OnHoverActionConfigurationChanged));

    public static readonly DependencyProperty HoverActionLabelsProperty = DependencyProperty.Register(
        nameof(HoverActionLabels),
        typeof(object),
        typeof(WinoMailListView),
        new PropertyMetadata(null, OnHoverActionConfigurationChanged));

    public static readonly DependencyProperty HoverActionCommandProperty = DependencyProperty.Register(
        nameof(HoverActionCommand),
        typeof(ICommand),
        typeof(WinoMailListView),
        new PropertyMetadata(null, OnHoverActionConfigurationChanged));

    public static readonly DependencyProperty HoverActionAnimationProperty = DependencyProperty.Register(
        nameof(HoverActionAnimation),
        typeof(HoverActionAnimation),
        typeof(WinoMailListView),
        new PropertyMetadata(HoverActionAnimation.Popup, OnHoverActionConfigurationChanged));

    public static readonly DependencyProperty HoverActionPositionProperty = DependencyProperty.Register(
        nameof(HoverActionPosition),
        typeof(HoverActionPosition),
        typeof(WinoMailListView),
        new PropertyMetadata(HoverActionPosition.RightCenter, OnHoverActionConfigurationChanged));

    public static readonly DependencyProperty HoverActionButtonSizeProperty = DependencyProperty.Register(
        nameof(HoverActionButtonSize),
        typeof(HoverActionButtonSize),
        typeof(WinoMailListView),
        new PropertyMetadata(HoverActionButtonSize.Small, OnHoverActionConfigurationChanged));

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

    public bool IsHoverActionsEnabled
    {
        get => (bool)GetValue(IsHoverActionsEnabledProperty);
        set => SetValue(IsHoverActionsEnabledProperty, value);
    }

    /// <summary>
    /// Hosts the list purely to show what a row looks like. Swipe affordances still render, but the
    /// operation behind them is not executed.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial bool IsPreviewMode { get; set; }

    partial void OnIsPreviewModeChanged(bool newValue) => ApplyConfigurationToRealizedContainers();

    /// <summary>
    /// When every selected mail disappears from the source, selects the row that now occupies
    /// the first selected row's visible position (or the last row when the list got shorter).
    /// The replacement is chosen in visible order before the empty selection would be
    /// published, so hosts never observe an empty snapshot and the viewport does not move.
    /// A wholesale reset (folder switch) never triggers it.
    /// </summary>
    [GeneratedDependencyProperty]
    public partial bool SelectAdjacentOnRemoval { get; set; }

    public HoverActionKind LeftHoverAction
    {
        get => (HoverActionKind)GetValue(LeftHoverActionProperty);
        set => SetValue(LeftHoverActionProperty, value);
    }

    public HoverActionKind CenterHoverAction
    {
        get => (HoverActionKind)GetValue(CenterHoverActionProperty);
        set => SetValue(CenterHoverActionProperty, value);
    }

    public HoverActionKind RightHoverAction
    {
        get => (HoverActionKind)GetValue(RightHoverActionProperty);
        set => SetValue(RightHoverActionProperty, value);
    }

    public object? HoverActionLabels
    {
        get => GetValue(HoverActionLabelsProperty);
        set => SetValue(HoverActionLabelsProperty, value);
    }

    public ICommand? HoverActionCommand
    {
        get => (ICommand?)GetValue(HoverActionCommandProperty);
        set => SetValue(HoverActionCommandProperty, value);
    }

    public HoverActionAnimation HoverActionAnimation
    {
        get => (HoverActionAnimation)GetValue(HoverActionAnimationProperty);
        set => SetValue(HoverActionAnimationProperty, value);
    }

    public HoverActionPosition HoverActionPosition
    {
        get => (HoverActionPosition)GetValue(HoverActionPositionProperty);
        set => SetValue(HoverActionPositionProperty, value);
    }

    public HoverActionButtonSize HoverActionButtonSize
    {
        get => (HoverActionButtonSize)GetValue(HoverActionButtonSizeProperty);
        set => SetValue(HoverActionButtonSizeProperty, value);
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
        _viewportAnchor = null;
        CompositionTarget.Rendering -= OnFirstFrameRendered;
        _pendingFrameTrace = null;
        DetachProjection();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Cleanup();
        SelectionChanged -= OnNativeSelectionChanged;
        ItemClick -= OnItemClick;
        ContainerContentChanging -= OnContainerContentChanging;
        _mailItemsSource = null;
        _contextMenuContainer = null;
        _pressedRow = null;
        _multiSelectRetainedItem = null;
        _selectionRestoreCompletion?.TrySetCanceled();
        _selectionRestoreCompletion = null;
        GC.SuppressFinalize(this);
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
        if (_disposed)
        {
            return;
        }

        _isTemplateApplied = true;
        _scrollViewer = GetTemplateChild(ScrollViewerPartName) as ScrollViewer;
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
            container.ResetHoverActions();
            ApplyHoverActionConfiguration(container);
        }
    }

    protected override void ClearContainerForItemOverride(DependencyObject element, object item)
    {
        if (element is WinoMailListViewItem container)
        {
            if (ReferenceEquals(container, _contextMenuContainer))
            {
                _contextMenuContainer = null;
            }

            container.ResetHoverActions();
            container.OwnerList = null;
            container.Row = null;
            container.HoverActionCommand = null;
        }

        base.ClearContainerForItemOverride(element, item);
    }

    /// <summary>
    /// Marks the row a context menu was opened from so its hover actions stay visible while the
    /// menu holds the pointer. Pass <see langword="null"/> when the menu closes.
    /// </summary>
    public void SetContextMenuOpenRow(MailListRow? row)
    {
        var container = row is null ? null : ContainerFromItem(row) as WinoMailListViewItem;

        if (ReferenceEquals(container, _contextMenuContainer))
        {
            return;
        }

        _contextMenuContainer?.SetContextMenuOpen(false);
        _contextMenuContainer = container;
        _contextMenuContainer?.SetContextMenuOpen(true);
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

    private static void OnHoverActionConfigurationChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        ((WinoMailListView)sender).ApplyConfigurationToRealizedContainers();
    }

    private void ApplyConfigurationToRealizedContainers()
    {
        foreach (var item in Items)
        {
            if (ContainerFromItem(item) is WinoMailListViewItem container)
            {
                ApplyHoverActionConfiguration(container);
            }
        }
    }

    private void ApplyHoverActionConfiguration(WinoMailListViewItem container)
    {
        container.LeftHoverAction = IsHoverActionsEnabled ? LeftHoverAction : HoverActionKind.None;
        container.CenterHoverAction = IsHoverActionsEnabled ? CenterHoverAction : HoverActionKind.None;
        container.RightHoverAction = IsHoverActionsEnabled ? RightHoverAction : HoverActionKind.None;
        container.HoverActionLabels = HoverActionLabels;
        container.HoverActionCommand = HoverActionCommand;
        container.HoverActionAnimation = HoverActionAnimation;
        container.HoverActionPosition = HoverActionPosition;
        container.HoverActionButtonSize = HoverActionButtonSize;
        container.AreSwipeOperationsEnabled = !IsPreviewMode;
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
        ItemsSource = DetachCurrency(viewSource.View);

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
        // A wholesale replacement is a new identity set, not a removal from the current one.
        _removalAnchor = default;
        _viewportAnchor = null;
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
        ItemsSource = DetachCurrency(GetViewSource().View);
    }

    /// <summary>
    /// A grouped collection view starts with its first row as the current item, and the list
    /// mirrors currency into its selection. Selection here is identity-token driven, so the
    /// view is attached without a current item; otherwise every wholesale reset would select
    /// the first row on its own.
    /// </summary>
    private static ICollectionView? DetachCurrency(ICollectionView? view)
    {
        if (view is not null && view.CurrentPosition >= 0)
        {
            view.MoveCurrentToPosition(-1);
        }

        return view;
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
        _removalAnchor = CaptureRemovalAnchor();
        _viewportAnchor = CaptureViewportAnchor();
        _focusWasInsideList = _removalAnchor.IsSet && IsFocusInsideList();
    }

    private bool IsFocusInsideList()
    {
        if (XamlRoot is null || FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused)
        {
            return false;
        }

        for (var current = focused; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, this))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Records where the rows at the top of the viewport sit before the projection changes.
    /// The items panel anchors the viewport on a realized container; when that container is
    /// the row being removed or replaced (a deleted thread head, a thread collapsing into a
    /// single row) the panel loses its anchor and the offset moves. The first surviving row
    /// of this list is scrolled back to the position it had, so what the user sees stays put.
    /// </summary>
    private ViewportAnchor? CaptureViewportAnchor()
    {
        if (_scrollViewer is null || _scrollViewer.VerticalOffset <= 0 || ItemsPanelRoot is null)
        {
            return null;
        }

        var viewportHeight = _scrollViewer.ViewportHeight;
        var candidates = new List<(Guid Id, double Top)>();
        foreach (var child in ItemsPanelRoot.Children)
        {
            if (child is not WinoMailListViewItem { Row: { } row } container)
            {
                continue;
            }

            var top = container.TransformToVisual(_scrollViewer).TransformPoint(new Point(0, 0)).Y;
            if (top + container.ActualHeight <= 0 || top >= viewportHeight)
            {
                continue;
            }

            candidates.Add((row.SourceItem.StableId, top));
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        candidates.Sort(static (left, right) => left.Top.CompareTo(right.Top));
        return new ViewportAnchor(candidates);
    }

    private void RestoreViewportAnchor()
    {
        var anchor = _viewportAnchor;
        _viewportAnchor = null;
        if (anchor is null || _scrollViewer is null || _projection is null || _disposed)
        {
            return;
        }

        // The items panel realizes and positions containers during layout; measure after a
        // synchronous pass so the positions are final rather than those of the previous frame.
        UpdateLayout();

        foreach (var (id, previousTop) in anchor.Candidates)
        {
            if (ResolveVisibleRow(id) is not { } row || ContainerFromItem(row) is not FrameworkElement container)
            {
                // Removed, or not realized yet: the next row down is the new reference.
                continue;
            }

            var top = container.TransformToVisual(_scrollViewer).TransformPoint(new Point(0, 0)).Y;
            var delta = top - previousTop;
            if (Math.Abs(delta) > 0.5)
            {
                _scrollViewer.ChangeView(null, _scrollViewer.VerticalOffset + delta, null, disableAnimation: true);
            }

            return;
        }
    }

    /// <summary>
    /// Remembers, before the projection changes, which mail follows and which precedes the
    /// selection in visible order, plus the first selected row's index. If the change removes
    /// every selected mail, the successor is the natural next selection; the predecessor covers
    /// a removal at the end of the list, and the index is the last resort when both vanished.
    /// Identities are used rather than indices because rows above the selection can disappear
    /// in the same change, for example when a thread collapses into a single row.
    /// </summary>
    private RemovalAnchor CaptureRemovalAnchor()
    {
        if (!SelectAdjacentOnRemoval || _projection is null || _tokens.Count == 0)
        {
            return default;
        }

        var index = 0;
        var firstSelectedIndex = -1;
        string? holdingThreadKey = null;
        Guid? holdingRepresentativeId = null;
        Guid? predecessor = null;
        foreach (var group in _projection.Groups)
        {
            foreach (var row in group)
            {
                var isSelected = IsSelectedOrHoldsSelectedLeaf(row);
                if (isSelected && firstSelectedIndex < 0)
                {
                    firstSelectedIndex = index;
                    if (!IsSelected(row))
                    {
                        // The selection lives in this collapsed thread. If the thread outlives
                        // the removal its head is the closest visible stand-in, and if it
                        // shrinks to a single row its representative is.
                        holdingThreadKey = row.ThreadKey;
                        holdingRepresentativeId = row.SourceItem.StableId;
                    }
                }
                else if (!isSelected && firstSelectedIndex < 0)
                {
                    predecessor = row.SourceItem.StableId;
                }
                else if (!isSelected)
                {
                    return new RemovalAnchor(true, firstSelectedIndex, holdingThreadKey, holdingRepresentativeId, row.SourceItem.StableId, predecessor);
                }

                index++;
            }
        }

        return firstSelectedIndex < 0
            ? default
            : new RemovalAnchor(true, firstSelectedIndex, holdingThreadKey, holdingRepresentativeId, null, predecessor);
    }

    /// <summary>
    /// A row counts as selected for anchoring when it is selected itself or when it is the
    /// collapsed head of a thread that holds a selected leaf, since that leaf has no row of its own.
    /// </summary>
    private bool IsSelectedOrHoldsSelectedLeaf(MailListRow row)
    {
        if (IsSelected(row))
        {
            return true;
        }

        if (!row.IsThreadHead || row.Thread is not { IsExpanded: false } thread)
        {
            return false;
        }

        foreach (var item in thread.Items)
        {
            if (_tokens.Contains(SelectionToken.ForItem(item)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Drops tokens whose mail no longer exists. When that empties the selection and a removal
    /// anchor was captured, the row now at the anchor takes over so the selection never goes
    /// through an empty state. Returns <see langword="true"/> when a replacement was selected.
    /// </summary>
    private bool ReconcileTokensAfterRemoval()
    {
        var anchor = _removalAnchor;
        _removalAnchor = default;
        if (_projection is null || _tokens.Count == 0)
        {
            return false;
        }

        var dropped = _tokens.RemoveWhere(token =>
            token.StableId is { } stableId
                ? _projection.FindItem(stableId) is null
                : token.ThreadKey is not null &&
                  _projection.FindThread(token.ThreadKey) is null);
        if (dropped == 0 ||
            _tokens.Count > 0 ||
            !anchor.IsSet ||
            !SelectAdjacentOnRemoval ||
            _projection.RowCount == 0)
        {
            return false;
        }

        var replacement =
            ResolveThreadHead(anchor.HoldingThreadKey) ??
            ResolveVisibleRow(anchor.HoldingRepresentativeId) ??
            ResolveVisibleRow(anchor.SuccessorId) ??
            ResolveVisibleRow(anchor.PredecessorId) ??
            _projection.GetRowAtVisibleIndex(Math.Min(anchor.Index, _projection.RowCount - 1));
        if (replacement is null)
        {
            return false;
        }

        _tokens.Add(SelectionToken.ForItem(replacement.SourceItem));
        return true;
    }

    private MailListRow? ResolveThreadHead(string? threadKey) =>
        threadKey is not null && _projection?.FindThread(threadKey) is { } thread
            ? _projection.FindRow(thread.RepresentativeItem.StableId)
            : null;

    /// <summary>
    /// The row that shows a mail, or the head of its thread when the mail is a collapsed leaf.
    /// </summary>
    private MailListRow? ResolveVisibleRow(Guid? stableId)
    {
        if (stableId is not { } id || _projection is null || _projection.FindItem(id) is null)
        {
            return null;
        }

        if (_projection.FindRow(id) is { } row)
        {
            return row;
        }

        return _projection.GetThreadForItem(id) is { } thread
            ? _projection.FindRow(thread.RepresentativeItem.StableId)
            : null;
    }

    private void OnProjectionChanged(object? sender, EventArgs args)
    {
        _isProjectionChanging = false;
        if (_viewportAnchor is not null)
        {
            RestoreViewportAnchor();
        }

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
        // While a restore is queued the native selection is transient: the projection just
        // changed and focus recovery may select whatever container took the focused slot.
        // The queued restore re-applies the identity tokens, so that interim state is ignored.
        if (_isProjectionChanging || _isRestoringSelection || _isSelectionRestoreQueued)
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
            // A list that does not select (a preview host, for example) rejects every write to
            // SelectedItems with a catastrophic failure, so there is nothing to restore there.
            if (_projection is null || SelectionMode == ListViewSelectionMode.None)
            {
                PublishSelectionSnapshot();
                return;
            }

            var replacedRemovedSelection = ReconcileTokensAfterRemoval();

            _isRestoringSelection = true;
            MailListRow[] desiredRows;
            try
            {
                desiredRows = _projection.Rows
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

            if (replacedRemovedSelection && desiredRows.Length > 0)
            {
                MoveFocusToReplacement(desiredRows[0]);
            }
            else
            {
                _focusWasInsideList = false;
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

    /// <summary>
    /// After a removal replaced the selection, keeps keyboard focus on the list by moving it
    /// to the replacement's container. Whether focus belonged to the list is decided before
    /// the change: removing the focused container makes XAML move focus to the next control
    /// in tab order (typically the reader), and that stray move must not win.
    /// </summary>
    private void MoveFocusToReplacement(MailListRow row)
    {
        var focusWasInsideList = _focusWasInsideList;
        _focusWasInsideList = false;
        if (focusWasInsideList && ContainerFromItem(row) is Control container)
        {
            container.Focus(FocusState.Programmatic);
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

    /// <summary>Rows near the top of the viewport and their offsets before a projection change.</summary>
    private sealed record ViewportAnchor(IReadOnlyList<(Guid Id, double Top)> Candidates);

    /// <summary>
    /// What to select when a change removes the whole selection, captured before the change.
    /// <see cref="IsSet"/> is explicit because the default value must not resolve to row zero.
    /// </summary>
    private readonly record struct RemovalAnchor(
        bool IsSet,
        int Index,
        string? HoldingThreadKey,
        Guid? HoldingRepresentativeId,
        Guid? SuccessorId,
        Guid? PredecessorId);

    private readonly record struct SelectionToken(string ThreadKey, Guid? StableId)
    {
        public static SelectionToken ForThread(string threadKey) => new(threadKey, null);

        public static SelectionToken ForItem(IMailListSourceItem item) =>
            new(item.ThreadKey ?? item.StableId.ToString("N"), item.StableId);
    }
}
