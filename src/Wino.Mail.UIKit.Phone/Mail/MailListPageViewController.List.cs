using System.ComponentModel;
using CoreFoundation;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Mail.Controls.UIKit.MailList;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Phone.Shell;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;
using Wino.Mail.UIKit.Views.Mail;

namespace Wino.Mail.UIKit.Phone.Mail;

public sealed partial class MailListPageViewController
{
    private static readonly TimeSpan ProjectionThrottle = TimeSpan.FromMilliseconds(120);

    /// <summary>How far the table's multiple-selection control indents a row's content in edit mode.</summary>
    private const double EditingIndent = 38;

    private readonly UITableView _table = new(default, UITableViewStyle.Plain) { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly Dictionary<string, MailListRow> _rowsByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sectionTitles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selectionKeys = new(StringComparer.Ordinal);
    private readonly WinoPivotBar _pivotBar = new() { FillsWidth = true };
    private readonly UIButton _filterButton = new(UIButtonType.System) { ShowsMenuAsPrimaryAction = true, TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UIButton _emptyFolderButton = new(UIButtonType.System) { Hidden = true, TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UIButton _selectButton = new(UIButtonType.System) { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UIView _headerRow = new() { TranslatesAutoresizingMaskIntoConstraints = false };
    private nfloat _lastScrollOffset;
    private nfloat _scrollTravel;
    private bool _headerCollapsed;
    private readonly WinoInfoBar _syncBar = new(WinoInfoBarSeverity.Informational, Translator.InfoBarTitle_SynchronizationDisabledFolder, Translator.InfoBarMessage_SynchronizationDisabledFolder)
    {
        ActionTitle = Translator.MacOS_MailList_EnableFolderSync,
        IsClosable = true
    };
    private readonly UIView _syncBarRow = new() { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
    private readonly UIButton _otherInboxButton = new(UIButtonType.System) { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UIView _otherInboxRow = new() { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
    private readonly UIButton _onlineSearchButton = new(UIButtonType.System) { TranslatesAutoresizingMaskIntoConstraints = false };
    private readonly UIView _onlineSearchRow = new() { TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
    private readonly WinoStateView _emptyState = new(WinoIconGlyph.Mail, Translator.NoMessageEmptyFolder, null) { Hidden = true };
    private readonly WinoStateView _loadingState = new(WinoIconGlyph.Mail, string.Empty, null) { Hidden = true };
    private readonly WinoInfoBar _undoBar = new() { Hidden = true, IsClosable = false, ActionTitle = Translator.UndoActions_Button };
    private readonly WinoSurfaceView _undoBarHost = new() { Fill = UIColor.SystemBackground, CornerRadius = WinoStyle.GroupRadius, Hidden = true };
    private readonly WinoInfoBar _infoBar = new() { Hidden = true, IsClosable = false, AutoDismissInterval = TimeSpan.FromSeconds(2) };
    private readonly WinoSurfaceView _infoBarHost = new() { Fill = UIColor.SystemBackground, CornerRadius = WinoStyle.GroupRadius, Hidden = true };
    private readonly UIRefreshControl _refresh = new();
    private UITableViewDiffableDataSource<NSString, NSString>? _dataSource;
    private ListDelegate? _delegate;
    private MailListProjection? _projection;
    private UIActivityIndicatorView? _loadingSpinner;
    private int _projectionApplyScheduled;
    private long _lastProjectionApply;
    private bool _restoringSelection;
    private bool _listBound;
    private bool _selectionMode;
    private bool _syncBarDismissed;
    private object? _syncBarFolder;
    private bool _refreshing;
    private string _lastPublishedSelection = string.Empty;

    // ---- Layout ----

    private void BuildList(MailListPage page)
    {
        // Header: the Focused/Other pivot as a segmented control, Empty folder (Junk) and the filter and sort pull-down.
        _pivotBar.SelectionChanged += (_, index) => PivotClicked(index);
        var filterConfiguration = UIButtonConfiguration.GrayButtonConfiguration;
        filterConfiguration.ButtonSize = UIButtonConfigurationSize.Small;
        filterConfiguration.Image = WinoIcons.TemplateImage(WinoIconGlyph.Filter, 14);
        filterConfiguration.ImagePadding = 4;
        _filterButton.Configuration = filterConfiguration;
        WinoAccessibility.Label(_filterButton, Translator.Accessibility_FilterMessages);
        WinoLayout.Priorities(_filterButton, UILayoutConstraintAxis.Horizontal, 1000, 1000);
        var emptyConfiguration = UIButtonConfiguration.PlainButtonConfiguration;
        emptyConfiguration.Image = WinoIcons.TemplateImage(WinoIconGlyph.EmptyFolder, 18);
        _emptyFolderButton.Configuration = emptyConfiguration;
        WinoAccessibility.Label(_emptyFolderButton, Translator.FolderOperation_Empty);
        _emptyFolderButton.PrimaryActionTriggered += (_, _) => Observe(ViewModel.EmptyFolderCommand.ExecuteAsync(null));
        WinoLayout.Priorities(_emptyFolderButton, UILayoutConstraintAxis.Horizontal, 1000, 1000);
        // The select button enters edit mode (Windows multi-select toggle).
        var selectConfiguration = UIButtonConfiguration.PlainButtonConfiguration;
        selectConfiguration.Image = WinoIcons.TemplateImage(WinoIconGlyph.CheckmarkCircle, 20);
        selectConfiguration.ContentInsets = new NSDirectionalEdgeInsets(6, 6, 6, 6);
        _selectButton.Configuration = selectConfiguration;
        WinoAccessibility.Label(_selectButton, Translator.Buttons_Multiselect);
        _selectButton.PrimaryActionTriggered += (_, _) => ViewModel.IsMultiSelectionModeEnabled = true;
        WinoLayout.Priorities(_selectButton, UILayoutConstraintAxis.Horizontal, 1000, 1000);
        var header = WinoLayout.Insets(WinoLayout.HStack(8, _pivotBar, WinoLayout.Spacer(), _emptyFolderButton, _filterButton, _selectButton), 6, 10, 2, 6);
        header.Alignment = UIStackViewAlignment.Center;
        WinoLayout.Fill(header, _headerRow);

        // Windows SyncDisabledInfoBar: shown while the folder does not synchronize; closing it lasts until the folder changes.
        _syncBar.ActionInvoked += (_, _) => Observe(ViewModel.EnableFolderSynchronizationCommand.ExecuteAsync(null));
        _syncBar.Closed += (_, _) => { _syncBarDismissed = true; UpdateSyncBar(); };
        WinoLayout.Fill(_syncBar, _syncBarRow, 2, 8, 6, 8);

        // Windows OtherInboxUnreadNoticeLink.
        var otherConfiguration = UIButtonConfiguration.PlainButtonConfiguration;
        otherConfiguration.TitleLineBreakMode = UILineBreakMode.WordWrap;
        otherConfiguration.ContentInsets = new NSDirectionalEdgeInsets(4, 6, 4, 6);
        _otherInboxButton.Configuration = otherConfiguration;
        _otherInboxButton.HorizontalAlignment = UIControlContentHorizontalAlignment.Leading;
        _otherInboxButton.PrimaryActionTriggered += (_, _) => Observe(ViewModel.OpenOtherInboxCommand.ExecuteAsync(null));
        WinoLayout.Fill(_otherInboxButton, _otherInboxRow, 0, 8, 4, 8);

        // "Try online search" under the results (Windows OnlineSearchTry).
        var onlineConfiguration = UIButtonConfiguration.GrayButtonConfiguration;
        onlineConfiguration.Image = WinoIcons.TemplateImage(WinoIconGlyph.GlobeSearch, 18);
        onlineConfiguration.ImagePadding = 10;
        onlineConfiguration.Title = Translator.OnlineSearchTry_Line1;
        onlineConfiguration.Subtitle = Translator.OnlineSearchTry_Line2;
        onlineConfiguration.TitleAlignment = UIButtonConfigurationTitleAlignment.Leading;
        _onlineSearchButton.Configuration = onlineConfiguration;
        _onlineSearchButton.HorizontalAlignment = UIControlContentHorizontalAlignment.Leading;
        _onlineSearchButton.PrimaryActionTriggered += (_, _) => Observe(RunOnlineSearchAsync());
        WinoLayout.Fill(_onlineSearchButton, _onlineSearchRow, 6, 10, 10, 10);

        // The table: Wino rows over the shared projection. The date headers have no background, so a header the table
        // pins at the top hides its label (WinoMailGroupHeaderView.HidesWhenPinned) and headers read as scrolling rows.
        _table.BackgroundColor = UIColor.Clear;
        _table.SeparatorStyle = UITableViewCellSeparatorStyle.None;
        _table.SectionHeaderTopPadding = 0;
        _table.EstimatedRowHeight = 90;
        _table.EstimatedSectionHeaderHeight = 0;
        _table.AllowsMultipleSelection = false;
        // Edit mode uses the table's own editing: selection circles, range drags over them and two-finger pans.
        _table.AllowsMultipleSelectionDuringEditing = true;
        _table.TintColor = WinoStyle.Accent;
        _table.RegisterClassForCellReuse(typeof(PhoneMailRowCell), WinoMailRowCell.ReuseIdentifier);
        _table.RegisterClassForHeaderFooterViewReuse(typeof(WinoMailGroupHeaderView), WinoMailGroupHeaderView.ReuseIdentifier);
        _table.RefreshControl = _refresh;
        _refresh.ValueChanged += (_, _) => StartRefresh();
        WinoAccessibility.Label(_table, Translator.Accessibility_MailList);
        _dataSource = new UITableViewDiffableDataSource<NSString, NSString>(_table, CreateCell) { DefaultRowAnimation = UITableViewRowAnimation.Fade };
        _delegate = new ListDelegate(this);
        _table.Delegate = _delegate;
        // Alongside the table's own touch handling (row highlight, scroll), so the press is not swallowed.
        var longPress = new UILongPressGestureRecognizer(RowLongPressed) { MinimumPressDuration = 0.45 };
        longPress.ShouldRecognizeSimultaneously = (_, _) => true;
        _table.AddGestureRecognizer(longPress);

        var listHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(_table, listHost);
        _emptyState.Place(listHost);
        _loadingState.Place(listHost);
        _loadingSpinner = _loadingState.UseSpinner();
        _loadingState.Title.Hidden = true;

        foreach (var view in new UIView[] { _headerRow, _syncBarRow, _otherInboxRow, ScopeBarRow, listHost, _onlineSearchRow })
            page.Column.AddArrangedSubview(view);

        // The undo bar stacks above the status bar; both float over the bottom of the list.
        _undoBar.ActionInvoked += (_, _) => Observe(ViewModel.UndoLatestQueuedActionCommand.ExecuteAsync(null));
        _undoBar.Closed += (_, _) => ViewModel.IsUndoMailActionBarOpen = false;
        _infoBar.Closed += (_, _) => ViewModel.IsBarOpen = false;
        foreach (var (bar, host) in new (UIView Bar, WinoSurfaceView Host)[] { (_undoBar, _undoBarHost), (_infoBar, _infoBarHost) })
        {
            host.Layer.ShadowColor = UIColor.Black.CGColor;
            host.Layer.ShadowOpacity = 0.18f;
            host.Layer.ShadowRadius = 10;
            host.Layer.ShadowOffset = new CoreGraphics.CGSize(0, 3);
            WinoLayout.Fill(bar, host);
            page.Overlay.AddArrangedSubview(host);
        }
    }

    private void BindList()
    {
        if (_listBound) return;
        _listBound = true;
        _projection = new MailListProjection(ViewModel.MailCollection.Items, ViewModel.MailListOptions ?? new MailListProjectionOptions());
        _projection.ProjectionChanged += ProjectionChanged;
        ApplyEntries(animate: false);

        Bind(nameof(ViewModel.ActiveFolder), vm => vm.ActiveFolder, _ => { SetHeaderCollapsed(false); UpdatePivots(); UpdateSyncBar(); UpdateOtherInboxLink(); UpdateTitle(); });
        Bind(nameof(ViewModel.SelectedFolderPivot), vm => vm.SelectedFolderPivot, _ => { UpdatePivots(); UpdateOtherInboxLink(); });
        Bind(nameof(ViewModel.IsEmptyFolderButtonVisible), vm => vm.IsEmptyFolderButtonVisible, visible => { _emptyFolderButton.Hidden = !visible; ApplyHeaderRow(); });
        Bind(nameof(ViewModel.SelectedFilterOption), vm => vm.SelectedFilterOption, _ => UpdateFilterMenu());
        Bind(nameof(ViewModel.SelectedSortingOption), vm => vm.SelectedSortingOption, _ => UpdateFilterMenu());
        ViewModel.PivotFolders.CollectionChanged += PivotFoldersChanged;
        Bind(nameof(ViewModel.MailListOptions), vm => vm.MailListOptions, options => _projection?.SetOptions(options ?? new MailListProjectionOptions()));
        Bind(nameof(ViewModel.IsMultiSelectionModeEnabled), vm => vm.IsMultiSelectionModeEnabled, ApplySelectionMode);
        Bind(nameof(ViewModel.IsInitializingFolder), vm => vm.IsInitializingFolder, _ => UpdateOverlays());
        Bind(nameof(ViewModel.IsEmpty), vm => vm.IsEmpty, _ => UpdateOverlays());
        Bind(nameof(ViewModel.IsInSearchMode), vm => vm.IsInSearchMode, _ => UpdateOverlays());
        Bind(nameof(ViewModel.IsOnlineSearchButtonVisible), vm => vm.IsOnlineSearchButtonVisible, visible => _onlineSearchRow.Hidden = !visible);
        Bind(nameof(ViewModel.IsBarOpen), vm => vm.IsBarOpen, _ => UpdateInfoBar());
        Bind(nameof(ViewModel.BarMessage), vm => vm.BarMessage, _ => UpdateInfoBar());
        Bind(nameof(ViewModel.BarTitle), vm => vm.BarTitle, _ => UpdateInfoBar());
        Bind(nameof(ViewModel.IsFolderSynchronizationEnabled), vm => vm.IsFolderSynchronizationEnabled, _ => UpdateSyncBar());
        Bind(nameof(ViewModel.IsSyncButtonVisible), vm => vm.IsSyncButtonVisible, _ => UpdateSyncBar());
        Bind(nameof(ViewModel.IsOtherInboxUnreadNoticeVisible), vm => vm.IsOtherInboxUnreadNoticeVisible, _ => UpdateOtherInboxLink());
        Bind(nameof(ViewModel.OtherInboxUnreadNoticeText), vm => vm.OtherInboxUnreadNoticeText, _ => UpdateOtherInboxLink());
        Bind(nameof(ViewModel.IsUndoMailActionBarOpen), vm => vm.IsUndoMailActionBarOpen, _ => UpdateUndoBar());
        Bind(nameof(ViewModel.UndoMailActionBarTitle), vm => vm.UndoMailActionBarTitle, _ => UpdateUndoBar());
        Bind(nameof(ViewModel.CurrentVisibleUndoMailActionPack), vm => vm.CurrentVisibleUndoMailActionPack, _ => RestartUndoCountdown());
        Bind(nameof(ViewModel.UndoMailActionBarDismissInterval), vm => vm.UndoMailActionBarDismissInterval, _ => RestartUndoCountdown());
        Bind(nameof(ViewModel.IsAccountSynchronizerInSynchronization), vm => vm.IsAccountSynchronizerInSynchronization, synchronizing =>
        {
            if (!synchronizing) EndRefresh();
        });
        Bind(nameof(ViewModel.SelectedItemsCount), vm => vm.SelectedItemsCount, _ =>
        {
            UpdatePivots();
            UpdateEditToolbar();
            UpdateTitle();
        });

        _preferences.PreferenceChanged += PreferenceChanged;
        _pictures.PictureChanged += SenderPictureChanged;
        WinoStyle.AccentChanged += AppearanceChanged;
        WinoStyle.BackdropChanged += AppearanceChanged;
        RegisterDebugCommands();
    }

    private void ReleaseList()
    {
        if (!_listBound) return;
        _listBound = false;
        _preferences.PreferenceChanged -= PreferenceChanged;
        _pictures.PictureChanged -= SenderPictureChanged;
        WinoStyle.AccentChanged -= AppearanceChanged;
        WinoStyle.BackdropChanged -= AppearanceChanged;
        ViewModel.PivotFolders.CollectionChanged -= PivotFoldersChanged;
        if (_projection is not null)
        {
            _projection.ProjectionChanged -= ProjectionChanged;
            _projection.Dispose();
            _projection = null;
        }
        foreach (var cell in _table.VisibleCells.OfType<WinoMailRowCell>())
        {
            cell.Subscription = null;
            cell.Item = null;
        }
        _rowsByKey.Clear();
        _sectionTitles.Clear();
        if (_dataSource is not null) _dataSource.ApplySnapshotUsingReloadData(new NSDiffableDataSourceSnapshot<NSString, NSString>());
    }

    private void PreferenceChanged(object? sender, string name)
        => OnUI(() =>
        {
            if (name is nameof(_preferences.IsShowPreviewEnabled) or nameof(_preferences.IsShowSenderPicturesEnabled)
                or nameof(_preferences.AccountNicknamePosition) or nameof(_preferences.MailTimeFormatPreference))
                RefreshVisibleCells(force: true);
            else if (name == nameof(_preferences.IsOtherInboxUnreadNoticeEnabled)) UpdateOtherInboxLink();
        });

    private void AppearanceChanged(object? sender, EventArgs args) => OnUI(() =>
    {
        if (!_listBound) return;
        RefreshVisibleCells(force: true);
        foreach (var header in VisibleHeaders()) header.RefreshTheme();
    });

    private IEnumerable<WinoMailGroupHeaderView> VisibleHeaders()
    {
        if (_dataSource is null) yield break;
        for (nint section = 0; section < _table.NumberOfSections(); section++)
            if (_table.GetHeaderView(section) is WinoMailGroupHeaderView header) yield return header;
    }

    // ---- Header: pivots and filters ----

    private void PivotFoldersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args) => OnUI(UpdatePivots);

    /// <summary>
    /// Outlook-style header (approach A of 18 Oct 2026): the Focused/Other, filter and Select row slides away after a
    /// 24pt drag down the list and comes back after a 12pt drag up, or at the top. Only the user's own scrolling counts,
    /// so a reload or a programmatic scroll never toggles it; edit mode keeps the row.
    /// </summary>
    internal void TrackHeaderCollapse(UIScrollView scrollView)
    {
        var offset = scrollView.ContentOffset.Y + scrollView.AdjustedContentInset.Top;
        var delta = offset - _lastScrollOffset;
        _lastScrollOffset = offset;
        if (!scrollView.Dragging && !scrollView.Decelerating) { _scrollTravel = 0; return; }
        if (offset <= 0 || _selectionMode) { SetHeaderCollapsed(false); return; }
        // Travel restarts whenever the direction changes.
        _scrollTravel = (delta > 0) == (_scrollTravel > 0) ? _scrollTravel + delta : delta;
        if (_scrollTravel > 24) SetHeaderCollapsed(true);
        else if (_scrollTravel < -12) SetHeaderCollapsed(false);
    }

    /// <summary>True when the folder has Focused and Other.</summary>
    private bool HasPivots => ViewModel.PivotFolders.Count > 1;

    private void SetHeaderCollapsed(bool collapsed)
    {
        if (_headerCollapsed == collapsed) return;
        _headerCollapsed = collapsed;
        ApplyHeaderRow(animated: !UIAccessibility.IsReduceMotionEnabled);
    }

    /// <summary>The header row shows for Focused/Other (or Empty folder) while the list is not scrolled away from it.</summary>
    private void ApplyHeaderRow(bool animated = false)
    {
        bool hidden = _headerCollapsed || !(HasPivots || ViewModel.IsEmptyFolderButtonVisible);
        _selectButton.Hidden = _selectionMode || !HasPivots;
        if (_headerRow.Hidden == hidden) return;
        if (!animated)
        {
            _headerRow.Hidden = hidden;
            _headerRow.Alpha = hidden ? 0 : 1;
            return;
        }
        UIView.Animate(0.22, 0, UIViewAnimationOptions.CurveEaseInOut | UIViewAnimationOptions.BeginFromCurrentState | UIViewAnimationOptions.AllowUserInteraction, () =>
        {
            _headerRow.Hidden = hidden;
            _headerRow.Alpha = hidden ? 0 : 1;
            _headerRow.Superview?.LayoutIfNeeded();
        }, null);
    }

    private void UpdatePivots()
    {
        var pivots = ViewModel.PivotFolders.ToArray();
        var items = pivots.Select(static pivot => new WinoPivotItem(pivot.FolderTitle, pivot.ShouldDisplaySelectedItemCount ? pivot.SelectedItemCount : 0)).ToArray();
        _pivotBar.SetItems(items, Array.IndexOf(pivots, ViewModel.SelectedFolderPivot));
        // A folder without Focused/Other has a single pivot: its own name, which the title already shows. Then the header
        // row goes away and Filter and Select move into the bar.
        _pivotBar.Hidden = pivots.Length < 2;
        _filterButton.Hidden = pivots.Length < 2;
        ApplyHeaderRow();
        ApplyNavigationItems();
    }

    /// <summary>Windows FolderPivotChanged: flag the pivots, clear the selection and run the pivot command.</summary>
    private void PivotClicked(int index)
    {
        var pivots = ViewModel.PivotFolders.ToArray();
        if (index < 0 || index >= pivots.Length || ReferenceEquals(pivots[index], ViewModel.SelectedFolderPivot)) return;
        foreach (var pivot in pivots) pivot.IsSelected = false;
        pivots[index].IsSelected = true;
        ViewModel.IsMultiSelectionModeEnabled = false;
        ClearSelection();
        ViewModel.SelectedFolderPivot = pivots[index];
        _pivotBar.SelectedIndex = index;
        Observe(ViewModel.SelectedPivotChangedCommand.ExecuteAsync(pivots[index]));
    }

    /// <summary>The filter pull-down: the shared filter and sorting options, then the search filter editor.</summary>
    private void UpdateFilterMenu()
    {
        var filters = ViewModel.FilterOptions.Select(option => (UIMenuElement)PhoneBarItems.Action(option.Title, WinoIconGlyph.None,
            () => Observe(ViewModel.SelectedFilterChangedCommand.ExecuteAsync(option)), on: ReferenceEquals(option, ViewModel.SelectedFilterOption))).ToArray();
        var sorting = ViewModel.SortingOptions.Select(option => (UIMenuElement)PhoneBarItems.Action(option.Title, WinoIconGlyph.None,
            () => Observe(ViewModel.SelectedSortingChangedCommand.ExecuteAsync(option)), on: ReferenceEquals(option, ViewModel.SelectedSortingOption))).ToArray();
        var searchFilters = PhoneBarItems.Action(Translator.MacOS_MailList_SearchFilters, WinoIconGlyph.Filter, ShowSearchFilters);
        _filterButton.Menu = UIMenu.Create([PhoneBarItems.Inline(filters), PhoneBarItems.Inline(sorting), PhoneBarItems.Inline(searchFilters)]);
        if (_filterBarItem is { } barItem) barItem.Menu = _filterButton.Menu;
        if (_filterButton.Configuration is { } configuration)
        {
            configuration.Title = ViewModel.SelectedFilterOption?.Title ?? string.Empty;
            _filterButton.Configuration = configuration;
        }
    }

    // ---- Bars ----

    private void UpdateSyncBar()
    {
        var folder = ViewModel.ActiveFolder;
        if (!ReferenceEquals(folder, _syncBarFolder))
        {
            _syncBarFolder = folder;
            _syncBarDismissed = false;
        }
        bool show = folder is not null && ViewModel.IsSyncButtonVisible && !ViewModel.IsFolderSynchronizationEnabled && !_syncBarDismissed;
        _syncBar.Hidden = !show;
        _syncBarRow.Hidden = !show;
    }

    private void UpdateOtherInboxLink()
    {
        bool show = ViewModel.IsOtherInboxUnreadNoticeVisible;
        _otherInboxRow.Hidden = !show;
        if (!show || _otherInboxButton.Configuration is not { } configuration) return;
        configuration.Title = ViewModel.OtherInboxUnreadNoticeText ?? string.Empty;
        configuration.BaseForegroundColor = WinoStyle.Accent;
        _otherInboxButton.Configuration = configuration;
    }

    private void UpdateInfoBar()
    {
        bool open = ViewModel.IsBarOpen && !string.IsNullOrWhiteSpace(ViewModel.BarMessage);
        _infoBar.Title = ViewModel.BarTitle;
        _infoBar.Message = ViewModel.BarMessage;
        _infoBar.Severity = MapSeverity(ViewModel.BarSeverity);
        // Host first: the bar starts its auto-dismiss countdown only when it is really visible, so un-hiding it inside a
        // still-hidden host left it on screen for good.
        _infoBarHost.Hidden = !open;
        _infoBar.Hidden = !open;
    }

    private void UpdateUndoBar()
    {
        bool open = ViewModel.IsUndoMailActionBarOpen;
        _undoBar.Title = ViewModel.UndoMailActionBarTitle;
        _undoBar.Severity = MapSeverity(ViewModel.UndoMailActionBarSeverity);
        _undoBar.AutoDismissInterval = TimeSpan.FromSeconds(ViewModel.UndoMailActionBarDismissInterval);
        _undoBarHost.Hidden = !open;
        _undoBar.Hidden = !open;
    }

    /// <summary>A new pack or interval while the bar is up gets a full countdown again.</summary>
    private void RestartUndoCountdown()
    {
        UpdateUndoBar();
        if (ViewModel.IsUndoMailActionBarOpen) _undoBar.RestartAutoDismiss();
    }

    private static WinoInfoBarSeverity MapSeverity(InfoBarMessageType severity) => severity switch
    {
        InfoBarMessageType.Success => WinoInfoBarSeverity.Success,
        InfoBarMessageType.Warning => WinoInfoBarSeverity.Warning,
        InfoBarMessageType.Error => WinoInfoBarSeverity.Error,
        _ => WinoInfoBarSeverity.Informational
    };

    private void UpdateOverlays()
    {
        bool empty = _rowsByKey.Count == 0;
        bool loading = ViewModel.IsInitializingFolder && empty;
        _loadingState.Hidden = !loading;
        if (loading) _loadingSpinner?.StartAnimating();
        else _loadingSpinner?.StopAnimating();
        _emptyState.Title.Text = ViewModel.IsInSearchMode ? Translator.NoMessageCrieteria : Translator.NoMessageEmptyFolder;
        _emptyState.Icon.Icon = ViewModel.IsInSearchMode ? WinoIconGlyph.Find : WinoIconGlyph.Mail;
        _emptyState.Hidden = !(empty && !ViewModel.IsInitializingFolder);
    }

    // ---- Pull to refresh ----

    private void StartRefresh()
    {
        _refreshing = true;
        if (ViewModel.SyncFolderCommand.CanExecute(null)) ViewModel.SyncFolderCommand.Execute(null);
        // The synchronizer reports its end through IsAccountSynchronizerInSynchronization; a folder that cannot
        // synchronize (categories, disabled) never starts, so the spinner stops on its own shortly after.
        DispatchQueue.MainQueue.DispatchAfter(new DispatchTime(DispatchTime.Now, TimeSpan.FromSeconds(1.5)), () =>
        {
            if (!ViewModel.IsAccountSynchronizerInSynchronization) EndRefresh();
        });
        DispatchQueue.MainQueue.DispatchAfter(new DispatchTime(DispatchTime.Now, TimeSpan.FromSeconds(30)), EndRefresh);
    }

    private void EndRefresh()
    {
        if (!_refreshing) return;
        _refreshing = false;
        if (_refresh.Refreshing) _refresh.EndRefreshing();
    }

    // ---- Projection to table ----

    /// <summary>Coalesces projection changes (synchronization publishes many small batches), like the Mac list.</summary>
    private void ProjectionChanged(object? sender, EventArgs args)
    {
        if (_released || Interlocked.Exchange(ref _projectionApplyScheduled, 1) == 1) return;
        var sinceLast = System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastProjectionApply));
        var delay = sinceLast >= ProjectionThrottle ? TimeSpan.Zero : ProjectionThrottle - sinceLast;
        DispatchQueue.MainQueue.DispatchAfter(new DispatchTime(DispatchTime.Now, delay), () =>
        {
            Interlocked.Exchange(ref _projectionApplyScheduled, 0);
            if (_released || !_listBound) return;
            Interlocked.Exchange(ref _lastProjectionApply, System.Diagnostics.Stopwatch.GetTimestamp());
            ApplyEntries(animate: true);
        });
    }

    /// <summary>
    /// Publishes the projection as a diffable snapshot keyed by the rows' stable identity (thread key or mail id), so
    /// realized cells survive synchronization batches and removals animate. Section keys are the date group titles.
    /// </summary>
    private void ApplyEntries(bool animate)
    {
        if (_projection is null || _dataSource is null) return;
        int previousCount = _rowsByKey.Count;
        int previousSelectionCount = _selectionKeys.Count;
        int previousFirst = FirstSelectedPosition();
        _rowsByKey.Clear();
        _sectionTitles.Clear();
        var snapshot = new NSDiffableDataSourceSnapshot<NSString, NSString>();
        bool grouped = (ViewModel.MailListOptions?.GroupMode ?? MailListGroupMode.Date) != MailListGroupMode.None;
        int sectionIndex = 0;
        foreach (var group in _projection.Groups)
        {
            if (group.Count == 0) continue;
            var title = grouped ? MailRowMapper.GroupTitle(group.Key) : string.Empty;
            var sectionKey = grouped && !string.IsNullOrEmpty(title) ? "G:" + title : "S:" + sectionIndex;
            if (_sectionTitles.ContainsKey(sectionKey)) sectionKey += "#" + sectionIndex;
            var items = new List<NSString>(group.Count);
            foreach (var row in group)
            {
                var key = SelectionKey(row);
                if (_rowsByKey.TryAdd(key, row)) items.Add(new NSString(key));
            }
            if (items.Count == 0) continue;
            _sectionTitles[sectionKey] = title;
            var section = new NSString(sectionKey);
            snapshot.AppendSections([section]);
            snapshot.AppendItems(items.ToArray(), section);
            sectionIndex++;
        }

        // Large swaps (a folder load, a re-sort) reload; small batches animate in place.
        bool animated = animate && _table.Window is not null && previousCount > 0 && Math.Abs(_rowsByKey.Count - previousCount) < 40;
        _restoringSelection = true;
        try
        {
            if (animated) _dataSource.ApplySnapshot(snapshot, true);
            else _dataSource.ApplySnapshotUsingReloadData(snapshot);
            RestoreSelection();
            SelectNeighbourAfterRemoval(previousCount, previousSelectionCount, previousFirst);
        }
        finally { _restoringSelection = false; }

        RefreshVisibleCells(force: false);
        SyncSelectionFromTable();
        UpdateOverlays();
        CheckLoadMore();
    }

    /// <summary>Re-selects the rows whose keys are selected (the snapshot can move them).</summary>
    private void RestoreSelection()
    {
        if (_dataSource is null) return;
        foreach (var indexPath in _table.IndexPathsForSelectedRows ?? [])
            if (KeyAt(indexPath) is not { } key || !_selectionKeys.Contains(key)) _table.DeselectRow(indexPath, false);
        foreach (var key in _selectionKeys)
            if (_dataSource.GetIndexPath(new NSString(key)) is { } indexPath) _table.SelectRow(indexPath, false, UITableViewScrollPosition.None);
    }

    private int FirstSelectedPosition()
    {
        if (_dataSource is null || _selectionKeys.Count == 0) return -1;
        var keys = _dataSource.Snapshot.ItemIdentifiers;
        for (int index = 0; index < keys.Length; index++)
            if (_selectionKeys.Contains(keys[index].ToString())) return index;
        return -1;
    }

    /// <summary>
    /// Beside the item zone (iPad landscape), removing the open message opens the row that takes its place when the
    /// preference asks for it, as on the Mac. The iPhone returns to the list instead.
    /// </summary>
    private void SelectNeighbourAfterRemoval(int previousCount, int previousSelectionCount, int previousFirst)
    {
        if (_dataSource is null || previousFirst < 0 || (_table.IndexPathsForSelectedRows?.Length ?? 0) > 0) return;
        int removed = previousCount - _rowsByKey.Count;
        if (removed <= 0 || removed > previousSelectionCount + 1) return;
        if (NavigationController is not ModeStackController { IsPaired: true } || !_preferences.AutoSelectNextItem
            || ViewModel.IsInitializingFolder || _selectionMode) return;
        var keys = _dataSource.Snapshot.ItemIdentifiers;
        if (keys.Length == 0) return;
        if (_dataSource.GetIndexPath(keys[Math.Min(previousFirst, keys.Length - 1)]) is { } indexPath)
            _table.SelectRow(indexPath, false, UITableViewScrollPosition.None);
    }

    /// <summary>Rebinds realized cells whose row object changed (or all of them) and re-measures rows whose tile line changed.</summary>
    private void RefreshVisibleCells(bool force)
    {
        bool heightsChanged = false;
        foreach (var cell in _table.VisibleCells.OfType<WinoMailRowCell>())
        {
            if (_table.IndexPathForCell(cell) is not { } indexPath || RowAt(indexPath) is not { } row) continue;
            if (!force && ReferenceEquals(cell.Item, row)) continue;
            var before = cell.Model;
            BindCell(cell, row);
            heightsChanged |= TileLayoutChanged(before, cell.Model);
        }
        if (heightsChanged) NoteHeightsChanged();
    }

    private static bool TileLayoutChanged(WinoMailRowModel before, WinoMailRowModel after)
        => before.HasTileLine != after.HasTileLine || (after.HasTileLine && !WinoMailRowMeasure.SameTileLayout(before.Tiles, after.Tiles));

    /// <summary>Re-queries row heights without reloading cells.</summary>
    private void NoteHeightsChanged()
    {
        UIView.PerformWithoutAnimation(() =>
        {
            _table.BeginUpdates();
            _table.EndUpdates();
        });
    }

    internal static string SelectionKey(MailListRow row)
        => row.IsThreadHead ? "T:" + row.ThreadKey : "I:" + row.SourceItem.StableId.ToString("N");

    // The diffable source answers nil for paths it does not have. Asking the table for its counts here would
    // re-enter its row-height pass (the delegate's height callbacks come through this method).
    private string? KeyAt(NSIndexPath indexPath) => _dataSource?.GetItemIdentifier(indexPath)?.ToString();

    private MailListRow? RowAt(NSIndexPath indexPath) => KeyAt(indexPath) is { } key && _rowsByKey.TryGetValue(key, out var row) ? row : null;

    private NSIndexPath? IndexPathOf(MailListRow row) => _dataSource?.GetIndexPath(new NSString(SelectionKey(row)));

    /// <summary>Rows in display order (the debug bridge and "select all").</summary>
    private IEnumerable<(NSIndexPath IndexPath, MailListRow Row)> DisplayedRows()
    {
        if (_dataSource is null) yield break;
        foreach (var key in _dataSource.Snapshot.ItemIdentifiers)
            if (_rowsByKey.TryGetValue(key.ToString(), out var row) && _dataSource.GetIndexPath(key) is { } indexPath) yield return (indexPath, row);
    }

    // ---- Selection ----

    private void SyncSelectionFromTable()
    {
        if (_projection is null) return;
        var selectedRows = new List<MailListRow>();
        foreach (var indexPath in _table.IndexPathsForSelectedRows ?? [])
            if (RowAt(indexPath) is { } row) selectedRows.Add(row);

        _selectionKeys.Clear();
        foreach (var row in selectedRows) _selectionKeys.Add(SelectionKey(row));

        var result = MailListSelectionSnapshotBuilder.Build(selectedRows, _projection, null);
        if (result.Signature == _lastPublishedSelection) return;
        _lastPublishedSelection = result.Signature;
        ViewModel.ApplyMailSelectionSnapshot(result.Snapshot);
    }

    /// <summary>Drops the table selection and publishes the empty selection.</summary>
    private void ClearSelection()
    {
        foreach (var indexPath in _table.IndexPathsForSelectedRows ?? []) _table.DeselectRow(indexPath, false);
        SyncSelectionFromTable();
    }

    private void SelectAllRows()
    {
        foreach (var (indexPath, _) in DisplayedRows()) _table.SelectRow(indexPath, false, UITableViewScrollPosition.None);
        SyncSelectionFromTable();
    }

    private void ToggleThread(MailListRow row)
    {
        if (_projection is null || row.Thread is null) return;
        if (row.IsExpanded) _projection.CollapseThread(row.Thread.Key);
        else _projection.ExpandThread(row.Thread.Key);
    }

    private void CheckLoadMore()
    {
        if (!_listBound || _rowsByKey.Count == 0) return;
        var remaining = _table.ContentSize.Height - (_table.ContentOffset.Y + _table.Bounds.Height);
        if (remaining > 600) return;
        var command = ViewModel.LoadMoreItemsCommand;
        if (command.CanExecute(null)) Observe(command.ExecuteAsync(null));
    }

    // ---- Cells ----

    private nfloat RowHeight(MailListRow row)
    {
        if (!MailRowMapper.HasTiles(row)) return (nfloat)WinoMailRowModel.HeightFor(MailRowMapper.PhoneDensity, Array.Empty<WinoMailRowTile>(), 0);
        var kind = row.Kind == MailListRowKind.ThreadChild ? WinoMailRowKind.ThreadChild : WinoMailRowKind.Single;
        // Edit mode: the table's selection control indents the row content instead of the row's own check.
        var width = (double)_table.Bounds.Width - (_selectionMode ? EditingIndent : 0);
        var textWidth = WinoMailRowMetrics.TextWidth(width, MailRowMapper.PhoneDensity, kind, _preferences.IsShowSenderPicturesEnabled, false);
        return (nfloat)WinoMailRowModel.HeightFor(MailRowMapper.PhoneDensity, MailRowMapper.Tiles(row), textWidth);
    }

    private UITableViewCell CreateCell(UITableView tableView, NSIndexPath indexPath, NSObject identifier)
    {
        var cell = (PhoneMailRowCell)tableView.DequeueReusableCell(WinoMailRowCell.ReuseIdentifier, indexPath);
        if (!cell.IsWired)
        {
            cell.IsWired = true;
            cell.SetLabels(Translator.MailOperation_Archive, Translator.MailOperation_Delete, Translator.MailOperation_SetFlag,
                Translator.MailOperation_ClearFlag, Translator.MailOperation_MarkAsRead, Translator.MailOperation_MarkAsUnread, Translator.MailOperation_MoveJunk);
            cell.HoverActionsEnabled = false;
            // The table draws the selection circles in edit mode.
            cell.UsesSystemSelectionControl = true;
            cell.ThreadToggleRequested += (sender, _) =>
            {
                if ((sender as WinoMailRowCell)?.Item is MailListRow toggled) ToggleThread(toggled);
            };
            cell.CheckboxToggled += (sender, _) =>
            {
                if (sender is not WinoMailRowCell toggledCell || _table.IndexPathForCell(toggledCell) is not { } path) return;
                if (toggledCell.Selected) _table.DeselectRow(path, false);
                else _table.SelectRow(path, false, UITableViewScrollPosition.None);
                SyncSelectionFromTable();
            };
        }
        if (_rowsByKey.TryGetValue(identifier.ToString(), out var row)) BindCell(cell, row);
        return cell;
    }

    private WinoMailRowModel? MapRow(MailListRow row)
    {
        if (MailRowMapper.TryMap(row, _preferences, ViewModel.IsMergedAccountView, MailRowMapper.PhoneDensity) is not { } model) return null;
        return model.ShowPicture ? model with { Picture = _pictures.Get(model.SenderAddress) } : model;
    }

    /// <summary>A sender picture arrived: re-apply the visible rows from that sender (all rows when null).</summary>
    private void SenderPictureChanged(object? sender, string? address) => OnUI(() =>
    {
        if (!_listBound) return;
        foreach (var cell in _table.VisibleCells.OfType<WinoMailRowCell>())
        {
            if (cell.Item is not MailListRow row) continue;
            if (address is not null && !string.Equals(cell.Model.SenderAddress?.Trim(), address, StringComparison.OrdinalIgnoreCase)) continue;
            if (MapRow(row) is { } model) cell.Apply(model);
        }
    });

    /// <summary>Shows <paramref name="row"/> in the cell and follows its leaves' changes, coalesced per main-loop turn.</summary>
    private void BindCell(WinoMailRowCell cell, MailListRow row)
    {
        cell.Item = row;
        cell.IsSelectionMode = _selectionMode;
        if (MapRow(row) is { } initial) cell.Apply(initial);

        var leaves = (row.IsThreadHead ? row.LeafItems : new[] { row.SourceItem }).OfType<INotifyPropertyChanged>().ToArray();
        var shown = cell.Model;
        int pending = 0;
        bool rebind = false;
        bool active = true;
        void Schedule(bool leavesChanged)
        {
            if (leavesChanged) rebind = true;
            if (Interlocked.Exchange(ref pending, 1) == 1) return;
            DispatchQueue.MainQueue.DispatchAsync(() =>
            {
                Interlocked.Exchange(ref pending, 0);
                if (!active || _released || !ReferenceEquals(cell.Item, row)) return;
                if (rebind) BindCell(cell, row);
                else if (MapRow(row) is { } model) cell.Apply(model);
                if (TileLayoutChanged(shown, cell.Model)) NoteHeightsChanged();
                shown = cell.Model;
            });
        }
        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName is nameof(MailItemViewModel.IsSelected) or nameof(MailItemViewModel.ThumbnailUpdatedEvent)) return;
            Schedule(false);
        };
        foreach (var leaf in leaves) leaf.PropertyChanged += handler;
        PropertyChangedEventHandler rowHandler = (_, args) => Schedule(args.PropertyName is nameof(MailListRow.Thread) or nameof(MailListRow.LeafItems));
        row.PropertyChanged += rowHandler;
        cell.Subscription = new ActionDisposable(() =>
        {
            active = false;
            foreach (var leaf in leaves) leaf.PropertyChanged -= handler;
            row.PropertyChanged -= rowHandler;
        });
    }

    private UIView HeaderView(UITableView tableView, nint section)
    {
        if (_dataSource is null || _dataSource.GetSectionIdentifier(section) is not { } identifier
            || !_sectionTitles.TryGetValue(identifier.ToString(), out var title) || string.IsNullOrEmpty(title)) return new UIView();
        var header = tableView.DequeueReusableHeaderFooterView(WinoMailGroupHeaderView.ReuseIdentifier) as WinoMailGroupHeaderView ?? new WinoMailGroupHeaderView();
        header.Title = title;
        header.RefreshTheme();
        return header;
    }

    private nfloat HeaderHeight(nint section)
        => _dataSource is not null && _dataSource.GetSectionIdentifier(section) is { } identifier
           && _sectionTitles.TryGetValue(identifier.ToString(), out var title) && !string.IsNullOrEmpty(title)
            ? (nfloat)WinoMailRowMeasure.GroupRowHeight
            : 0;

    private void TableSelectionChanged(NSIndexPath indexPath, bool selected)
    {
        // Outside edit mode a tap replaces the selection; the deselection half of it is not a change of its own.
        if (_restoringSelection || (!selected && !_selectionMode)) return;
        SyncSelectionFromTable();
    }

    /// <summary>The list's row cell: <see cref="IsWired"/> marks a reused cell whose events already reach the page.</summary>
    private sealed class PhoneMailRowCell : WinoMailRowCell
    {
        [Export("initWithStyle:reuseIdentifier:")]
        public PhoneMailRowCell(UITableViewCellStyle style, NSString reuseIdentifier) : base(style, reuseIdentifier) { }

        public bool IsWired { get; set; }
    }

    /// <summary>The table delegate: heights, headers, selection, swipe actions, context menus and paging.</summary>
    private sealed class ListDelegate(MailListPageViewController owner) : UITableViewDelegate
    {
        public override nfloat GetHeightForRow(UITableView tableView, NSIndexPath indexPath)
            => owner.RowAt(indexPath) is { } row ? owner.RowHeight(row) : 0;

        public override UIView GetViewForHeader(UITableView tableView, nint section) => owner.HeaderView(tableView, section);

        public override nfloat GetHeightForHeader(UITableView tableView, nint section) => owner.HeaderHeight(section);

        public override nfloat GetHeightForFooter(UITableView tableView, nint section) => 0;

        /// <summary>Outside multi-select a conversation head expands or collapses; its messages open from their own rows.</summary>
        public override NSIndexPath? WillSelectRow(UITableView tableView, NSIndexPath indexPath)
        {
            if (owner._selectionMode || owner.RowAt(indexPath) is not { IsThreadHead: true } row) return indexPath;
            owner.ToggleThread(row);
            return null;
        }

        public override void RowSelected(UITableView tableView, NSIndexPath indexPath) => owner.TableSelectionChanged(indexPath, true);

        public override void RowDeselected(UITableView tableView, NSIndexPath indexPath) => owner.TableSelectionChanged(indexPath, false);

        public override UISwipeActionsConfiguration? GetLeadingSwipeActionsConfiguration(UITableView tableView, NSIndexPath indexPath)
            => owner.SwipeActions(indexPath, leading: true);

        public override UISwipeActionsConfiguration? GetTrailingSwipeActionsConfiguration(UITableView tableView, NSIndexPath indexPath)
            => owner.SwipeActions(indexPath, leading: false);

        public override void CellDisplayingEnded(UITableView tableView, UITableViewCell cell, NSIndexPath indexPath)
        {
            if (cell is not WinoMailRowCell mailCell) return;
            mailCell.Subscription = null;
            mailCell.Item = null;
        }

        public override void WillDisplay(UITableView tableView, UITableViewCell cell, NSIndexPath indexPath)
        {
            // A cell that scrolled back in after CellDisplayingEnded lost its binding.
            if (cell is WinoMailRowCell { Item: null } mailCell && owner.RowAt(indexPath) is { } row) owner.BindCell(mailCell, row);
        }

        public override void Scrolled(UIScrollView scrollView)
        {
            owner.CheckLoadMore();
            owner.TrackHeaderCollapse(scrollView);
        }

        // Range selection like Mail and Photos: a two-finger pan anywhere enters edit mode and selects the rows it crosses.
        public override bool ShouldBeginMultipleSelectionInteraction(UITableView tableView, NSIndexPath indexPath) => true;

        public override void DidBeginMultipleSelectionInteraction(UITableView tableView, NSIndexPath indexPath) => owner.BeginSelectionByInteraction();
    }
}
