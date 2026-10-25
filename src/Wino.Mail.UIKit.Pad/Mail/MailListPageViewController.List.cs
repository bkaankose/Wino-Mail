using System.ComponentModel;
using CoreFoundation;
using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.MailItem;
using Wino.Core.Domain.Models.Reader;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.HoverActions;
using Wino.Mail.Controls.UIKit.Common;
using Wino.Mail.Controls.UIKit.MailList;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.UIKit;
using Wino.Mail.UIKit.Views.Mail;

namespace Wino.Mail.UIKit.Pad.Mail;

public sealed partial class MailListPageViewController
{
    /// <summary>A table section: a date group header (empty when the list is not grouped) and its projected rows.</summary>
    private sealed record ListGroup(string Title, MailListRow[] Rows);

    private static readonly TimeSpan ProjectionThrottle = TimeSpan.FromMilliseconds(120);

    private readonly List<ListGroup> _groups = new();
    private readonly HashSet<string> _selectionKeys = new(StringComparer.Ordinal);
    private UITableView _table = null!;
    private MailListProjection? _projection;
    private MailListTableSource? _source;
    private MailListDragDelegate? _dragDelegate;
    private UIButton _selectAllButton = null!;
    private WinoPivotBar _pivotBar = null!;
    private WinoIconView _onlineGlyph = null!;
    private UIButton _emptyFolderButton = null!;
    private UIButton _selectModeButton = null!;
    private UIButton _filterButton = null!;
    private WinoInfoBar _infoBar = null!;
    private WinoSurfaceView _infoBarHost = null!;
    private UIView _onlineSearchPanel = null!;
    private UILabel _emptyLabel = null!;
    private UIActivityIndicatorView _listProgress = null!;
#if DEBUG
    // Set only by the Debug bridge's density command; Release always follows the preference.
    private WinoMailRowDensity? _densityOverride;
#else
    private static WinoMailRowDensity? _densityOverride => null;
#endif
    private double _tileLayoutWidth;
    private int _projectionApplyScheduled;
    private long _lastProjectionApply;
    private bool _restoringSelection;
    private bool _listBound;
    private bool _selectionMode;
    private bool _allSelected;
    private string _lastPublishedSelection = string.Empty;
    private int _lastRowCount;

    private UIView BuildListPane()
    {
        // Header (Windows MailListPage "Pivot + Sync + Multi Select"): select-all check, folder pivot, online-search
        // glyph, Empty folder, the multi-select toggle and the filter and sort menu.
        _selectAllButton = GlyphButton(WinoIconGlyph.Circle, 18, Translator.Accessibility_SelectAllMessages, SelectAllToggled);
        _pivotBar = new WinoPivotBar();
        _pivotBar.SelectionChanged += (_, index) => PivotClicked(index);
        _onlineGlyph = new WinoIconView(WinoIconGlyph.GlobeSearch, 16, WinoStyle.SecondaryText) { Hidden = true };
        WinoAccessibility.Label(_onlineGlyph, Translator.SettingsAppPreferences_SearchMode_Online);
        _emptyFolderButton = GlyphButton(WinoIconGlyph.EmptyFolder, 18, Translator.FolderOperation_Empty, () => Observe(ViewModel.EmptyFolderCommand.ExecuteAsync(null)));
        _emptyFolderButton.Hidden = true;
        _selectModeButton = GlyphButton(WinoIconGlyph.MultiSelect, 17, Translator.Buttons_Multiselect, () => ViewModel.IsMultiSelectionModeEnabled = !ViewModel.IsMultiSelectionModeEnabled);
        _filterButton = GlyphButton(WinoIconGlyph.Filter, 17, Translator.Accessibility_FilterMessages, null);
        _filterButton.ShowsMenuAsPrimaryAction = true;
        UpdateFilterMenu();

        var header = WinoLayout.Insets(WinoLayout.HStack(4, _selectAllButton, _pivotBar, WinoLayout.Spacer(), _onlineGlyph, _emptyFolderButton, _selectModeButton, _filterButton), 6, 6, 6, 4);
        header.Alignment = UIStackViewAlignment.Center;

        // Windows "Update Info Bar": not closable, dismisses itself after two seconds and floats over the bottom of the
        // list, so it never moves the rows.
        _infoBar = new WinoInfoBar { Hidden = true, IsClosable = false, AutoDismissInterval = TimeSpan.FromSeconds(2) };
        _infoBar.Closed += (_, _) => ViewModel.IsBarOpen = false;
        _infoBarHost = FloatingHost(_infoBar);

        _table = new UITableView(CGRect.Empty, UITableViewStyle.Plain)
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            BackgroundColor = UIColor.Clear,
            SeparatorStyle = UITableViewCellSeparatorStyle.None,
            AllowsMultipleSelection = false,
            SectionHeaderTopPadding = 0,
            EstimatedRowHeight = 0,
            EstimatedSectionHeaderHeight = 0,
            AllowsFocus = true,
            SelectionFollowsFocus = true,
            DragInteractionEnabled = true
        };
        _table.RegisterClassForCellReuse(typeof(WinoMailRowCell), WinoMailRowCell.ReuseIdentifier);
        _table.RegisterClassForHeaderFooterViewReuse(typeof(WinoMailGroupHeaderView), WinoMailGroupHeaderView.ReuseIdentifier);
        WinoAccessibility.Label(_table, Translator.SettingsMessageList_Title);
        _source = new MailListTableSource(this);
        _table.Source = _source;
        _dragDelegate = new MailListDragDelegate(this);
        _table.DragDelegate = _dragDelegate;

        _emptyLabel = WinoStyle.Label(Translator.NoMessageEmptyFolder, WinoStyle.Scaled(20, UIFontWeight.UltraLight), WinoStyle.Informational.ColorWithAlpha(0.4f), 0);
        _emptyLabel.TextAlignment = UITextAlignment.Center;
        _emptyLabel.Hidden = true;
        _listProgress = new UIActivityIndicatorView(UIActivityIndicatorViewStyle.Medium) { TranslatesAutoresizingMaskIntoConstraints = false, HidesWhenStopped = true };

        var listHost = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(_table, listHost);
        listHost.AddSubview(_emptyLabel);
        listHost.AddSubview(_listProgress);
        // The undo bar stacks above the update bar; both float over the bottom of the list.
        var bottomBars = WinoLayout.VStack(4, BuildUndoBar(), _infoBarHost);
        bottomBars.Alignment = UIStackViewAlignment.Fill;
        listHost.AddSubview(bottomBars);
        BuildDragBanner(listHost);
        NSLayoutConstraint.ActivateConstraints(
        [
            bottomBars.LeadingAnchor.ConstraintEqualTo(listHost.LeadingAnchor, 8),
            bottomBars.TrailingAnchor.ConstraintEqualTo(listHost.TrailingAnchor, -8),
            bottomBars.BottomAnchor.ConstraintEqualTo(listHost.SafeAreaLayoutGuide.BottomAnchor, -8),
            _emptyLabel.CenterXAnchor.ConstraintEqualTo(listHost.CenterXAnchor),
            _emptyLabel.CenterYAnchor.ConstraintEqualTo(listHost.CenterYAnchor),
            _emptyLabel.WidthAnchor.ConstraintLessThanOrEqualTo(listHost.WidthAnchor, 1, -40),
            _listProgress.CenterXAnchor.ConstraintEqualTo(listHost.CenterXAnchor),
            _listProgress.CenterYAnchor.ConstraintEqualTo(listHost.CenterYAnchor)
        ]);
        WinoLayout.Priorities(listHost, UILayoutConstraintAxis.Vertical, 1, 250);

        _onlineSearchPanel = BuildOnlineSearchPanel();
        _onlineSearchPanel.Hidden = true;

        var stack = WinoLayout.VStack(0, BuildActionBar(), header, BuildSyncBar(), BuildOtherInboxLink(), BuildScopeBar(), listHost, _onlineSearchPanel);
        stack.Alignment = UIStackViewAlignment.Fill;
        var pane = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        // Windows: the zone pads 5 on the sides and top; the rows carry the remaining gutter.
        WinoLayout.Fill(stack, pane, 5, 5, 0, 5);
        return pane;
    }

    /// <summary>An opaque, shadowed base under a floating bar, so its translucent tint stays readable over the rows.</summary>
    private static WinoSurfaceView FloatingHost(UIView bar)
    {
        var host = new WinoSurfaceView { Fill = UIColor.SystemBackground, CornerRadius = WinoStyle.GroupRadius, Hidden = true };
        host.Layer.ShadowColor = UIColor.Black.CGColor;
        host.Layer.ShadowOpacity = 0.22f;
        host.Layer.ShadowRadius = 12;
        host.Layer.ShadowOffset = new CGSize(0, 3);
        WinoLayout.Fill(bar, host);
        return host;
    }

    private UIView BuildOnlineSearchPanel()
    {
        var host = new WinoSurfaceView { Fill = WinoStyle.SubtleFill, CornerRadius = WinoStyle.GroupRadius };
        var glyph = new WinoIconView(WinoIconGlyph.GlobeSearch, 18, WinoStyle.Accent);
        var text = WinoLayout.VStack(2,
            WinoStyle.Label(Translator.OnlineSearchTry_Line1, WinoStyle.BodyStrong, null, 0),
            WinoStyle.Label(Translator.OnlineSearchTry_Line2, WinoStyle.Caption, WinoStyle.SecondaryText, 0));
        var row = WinoLayout.Insets(WinoLayout.HStack(10, glyph, text), 10, 12, 10, 12);
        row.Alignment = UIStackViewAlignment.Center;
        WinoLayout.Fill(row, host);
        var button = new UIButton(UIButtonType.System) { TranslatesAutoresizingMaskIntoConstraints = false };
        button.PrimaryActionTriggered += (_, _) => Observe(RunOnlineSearchAsync());
        WinoAccessibility.Label(button, $"{Translator.OnlineSearchTry_Line1} {Translator.OnlineSearchTry_Line2}");
        WinoLayout.Fill(button, host);
        var outer = new UIView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(host, outer, 8, 8, 12, 8);
        return outer;
    }

    private static UIButton GlyphButton(WinoIconGlyph glyph, double size, string label, Action? action)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.Image(glyph, size, null, label);
        configuration.ContentInsets = new NSDirectionalEdgeInsets(6, 6, 6, 6);
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false, PointerInteractionEnabled = true };
        WinoAccessibility.Label(button, label);
        WinoLayout.Size(button, 36, 36);
        if (action is not null) button.PrimaryActionTriggered += (_, _) => action();
        return button;
    }

    private static void SetGlyph(UIButton button, WinoIconGlyph glyph, double size, UIColor? tint)
    {
        if (button.Configuration is not { } configuration) return;
        configuration.Image = WinoIcons.Image(glyph, size, tint, button.AccessibilityLabel);
        button.Configuration = configuration;
    }

    private void BindList()
    {
        if (_listBound) return;
        _listBound = true;
        _projection = new MailListProjection(ViewModel.MailCollection.Items, ViewModel.MailListOptions ?? new MailListProjectionOptions());
        _projection.ProjectionChanged += ProjectionChanged;
        ReloadNow();

        Bind(nameof(ViewModel.ActiveFolder), vm => vm.ActiveFolder?.FolderName ?? string.Empty, _ => UpdatePivots());
        Bind(nameof(ViewModel.SelectedFolderPivot), vm => vm.SelectedFolderPivot, _ => UpdatePivots());
        Bind(nameof(ViewModel.AreSearchResultsOnline), vm => vm.AreSearchResultsOnline, online => _onlineGlyph.Hidden = !online);
        Bind(nameof(ViewModel.IsEmptyFolderButtonVisible), vm => vm.IsEmptyFolderButtonVisible, visible => _emptyFolderButton.Hidden = !visible);
        Bind(nameof(ViewModel.SelectedFilterOption), vm => vm.SelectedFilterOption, _ => UpdateFilterMenu());
        Bind(nameof(ViewModel.SelectedSortingOption), vm => vm.SelectedSortingOption, _ => UpdateFilterMenu());
        ViewModel.PivotFolders.CollectionChanged += PivotFoldersChanged;
        Bind(nameof(ViewModel.MailListOptions), vm => vm.MailListOptions, options => _projection?.SetOptions(options ?? new MailListProjectionOptions()));
        Bind(nameof(ViewModel.IsMultiSelectionModeEnabled), vm => vm.IsMultiSelectionModeEnabled, ApplySelectionMode);
        Bind(nameof(ViewModel.IsInitializingFolder), vm => vm.IsInitializingFolder, _ => UpdateOverlays());
        Bind(nameof(ViewModel.IsEmpty), vm => vm.IsEmpty, _ => UpdateOverlays());
        Bind(nameof(ViewModel.IsFolderEmpty), vm => vm.IsFolderEmpty, _ => UpdateOverlays());
        Bind(nameof(ViewModel.IsInSearchMode), vm => vm.IsInSearchMode, _ => UpdateOverlays());
        Bind(nameof(ViewModel.IsOnlineSearchButtonVisible), vm => vm.IsOnlineSearchButtonVisible, visible => _onlineSearchPanel.Hidden = !visible);
        Bind(nameof(ViewModel.IsBarOpen), vm => vm.IsBarOpen, _ => UpdateInfoBar());
        Bind(nameof(ViewModel.BarMessage), vm => vm.BarMessage, _ => UpdateInfoBar());
        Bind(nameof(ViewModel.BarTitle), vm => vm.BarTitle, _ => UpdateInfoBar());
        Bind(nameof(ViewModel.SelectedItemsCount), vm => vm.SelectedItemsCount, _ =>
        {
            UpdatePivots();
            UpdateSelectAll();
            UpdateMultiSelectionOverlay();
            CommandStateChanged?.Invoke(this, EventArgs.Empty);
        });
        Bind(nameof(ViewModel.MailActionResolver), vm => vm.MailActionResolver, _ => RefreshHoverActions());
        BindBars();
        BindDrag();

        _preferences.PreferenceChanged += PreferenceChanged;
        _pictures.PictureChanged += SenderPictureChanged;
        WinoStyle.AccentChanged += AccentChanged;
        WinoStyle.BackdropChanged += AccentChanged;
        RegisterDebugCommands();
    }

    private void ReleaseList()
    {
        if (!_listBound) return;
        _listBound = false;
        _preferences.PreferenceChanged -= PreferenceChanged;
        _pictures.PictureChanged -= SenderPictureChanged;
        WinoStyle.AccentChanged -= AccentChanged;
        WinoStyle.BackdropChanged -= AccentChanged;
        ViewModel.PivotFolders.CollectionChanged -= PivotFoldersChanged;
        if (_projection is not null)
        {
            _projection.ProjectionChanged -= ProjectionChanged;
            _projection.Dispose();
            _projection = null;
        }
        _groups.Clear();
        if (_table is not null)
        {
            foreach (var cell in _table.VisibleCells.OfType<WinoMailRowCell>()) cell.Subscription = null;
            _table.Source = null;
            _table.DragDelegate = null;
            _table.ReloadData();
        }
        _source?.Dispose();
        _source = null;
        _dragDelegate?.Dispose();
        _dragDelegate = null;
    }

    private void PreferenceChanged(object? sender, string name)
        => OnUI(() =>
        {
            if (name is nameof(_preferences.MailItemDisplayMode) or nameof(_preferences.IsShowPreviewEnabled)
                or nameof(_preferences.IsShowSenderPicturesEnabled) or nameof(_preferences.IsHoverActionsEnabled)
                or nameof(_preferences.AccountNicknamePosition))
                ReloadNow();
            else BarsPreferenceChanged(name);
        });

    private void AccentChanged(object? sender, EventArgs args) => OnUI(() =>
    {
        if (!_listBound) return;
        ReloadNow();
        RefreshBarsAccent();
        UpdateSelectionModeButton();
    });

    private void PivotFoldersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args) => OnUI(UpdatePivots);

    private void UpdatePivots()
    {
        var pivots = ViewModel.PivotFolders.ToArray();
        var items = pivots.Select(static pivot => new WinoPivotItem(pivot.FolderTitle, pivot.ShouldDisplaySelectedItemCount ? pivot.SelectedItemCount : 0)).ToArray();
        _pivotBar.SetItems(items, Array.IndexOf(pivots, ViewModel.SelectedFolderPivot));
        _pivotBar.Hidden = pivots.Length == 0;
    }

    /// <summary>Windows FolderPivotChanged: flag the pivots, clear the selection and run the pivot command.</summary>
    private void PivotClicked(int index)
    {
        var pivots = ViewModel.PivotFolders.ToArray();
        if (index < 0 || index >= pivots.Length || ReferenceEquals(pivots[index], ViewModel.SelectedFolderPivot)) return;

        foreach (var pivot in pivots) pivot.IsSelected = false;
        pivots[index].IsSelected = true;
        ViewModel.IsMultiSelectionModeEnabled = false;
        DeselectAll();
        ViewModel.SelectedFolderPivot = pivots[index];
        _pivotBar.SelectedIndex = index;
        Observe(ViewModel.SelectedPivotChangedCommand.ExecuteAsync(pivots[index]));
    }

    /// <summary>The filter pull-down: filter options, sorting options and the search filter editor.</summary>
    private void UpdateFilterMenu()
    {
        var filters = ViewModel.FilterOptions.Select(option =>
        {
            var action = UIAction.Create(option.Title, null, null, _ => Observe(ViewModel.SelectedFilterChangedCommand.ExecuteAsync(option)));
            action.State = ReferenceEquals(option, ViewModel.SelectedFilterOption) ? UIMenuElementState.On : UIMenuElementState.Off;
            return (UIMenuElement)action;
        }).ToArray();
        var sorting = ViewModel.SortingOptions.Select(option =>
        {
            var action = UIAction.Create(option.Title, null, null, _ => Observe(ViewModel.SelectedSortingChangedCommand.ExecuteAsync(option)));
            action.State = ReferenceEquals(option, ViewModel.SelectedSortingOption) ? UIMenuElementState.On : UIMenuElementState.Off;
            return (UIMenuElement)action;
        }).ToArray();
        // Opens the search filter editor, so a filtered search can start without typing a query.
        var editor = UIAction.Create(Translator.MacOS_MailList_SearchFilters, WinoIcons.Image(WinoIconGlyph.Filter, 16), null, _ => ShowSearchFilters(_filterButton));
        _filterButton.Menu = UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline,
        [
            UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, filters),
            UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, sorting),
            UIMenu.Create(string.Empty, null, UIMenuIdentifier.None, UIMenuOptions.DisplayInline, [editor])
        ]);
        var title = ViewModel.SelectedFilterOption?.Title;
        _filterButton.AccessibilityValue = title;
    }

    private void SelectAllToggled()
    {
        if (_allSelected) DeselectAll();
        else SelectAllRows();
    }

    private void UpdateSelectAll()
    {
        _allSelected = ViewModel.IsAllItemsSelected;
        SetGlyph(_selectAllButton, _allSelected ? WinoIconGlyph.CheckmarkCircleFilled : WinoIconGlyph.Circle, 18, _allSelected ? WinoStyle.Accent : null);
    }

    private void UpdateInfoBar()
    {
        bool open = ViewModel.IsBarOpen && !string.IsNullOrWhiteSpace(ViewModel.BarMessage);
        _infoBar.Title = ViewModel.BarTitle;
        _infoBar.Message = ViewModel.BarMessage;
        _infoBar.Severity = MapSeverity(ViewModel.BarSeverity);
        // Host first: the bar starts its auto-dismiss countdown only when it is really visible.
        _infoBarHost.Hidden = !open;
        _infoBar.Hidden = !open;
    }

    private void UpdateOverlays()
    {
        bool empty = _lastRowCount == 0;
        bool loading = ViewModel.IsInitializingFolder && empty;
        if (loading) _listProgress.StartAnimating();
        else _listProgress.StopAnimating();
        _emptyLabel.Text = ViewModel.IsInSearchMode ? Translator.NoMessageCrieteria : Translator.NoMessageEmptyFolder;
        _emptyLabel.Hidden = !(empty && !ViewModel.IsInitializingFolder);
    }

    private void ApplySelectionMode(bool enabled)
    {
        _selectionMode = enabled;
        _table.AllowsMultipleSelection = enabled;
        _table.SelectionFollowsFocus = !enabled;
        UpdateSelectionModeButton();
        foreach (var cell in _table.VisibleCells.OfType<WinoMailRowCell>()) cell.IsSelectionMode = enabled;
        // The check narrows the text column, which can wrap the tile row onto another line.
        ScheduleTileRelayout(force: true);
    }

    private void UpdateSelectionModeButton()
        => SetGlyph(_selectModeButton, WinoIconGlyph.MultiSelect, 17, _selectionMode ? WinoStyle.Accent : null);

    private void SelectAllRows()
    {
        if (!_selectionMode) ViewModel.IsMultiSelectionModeEnabled = true;
        _restoringSelection = true;
        try
        {
            for (int section = 0; section < _groups.Count; section++)
                for (int row = 0; row < _groups[section].Rows.Length; row++)
                    _table.SelectRow(NSIndexPath.FromRowSection(row, section), false, UITableViewScrollPosition.None);
        }
        finally { _restoringSelection = false; }
        SyncSelectionFromTable();
    }

    private void DeselectAll()
    {
        if (_table is null) return;
        _restoringSelection = true;
        try
        {
            foreach (var path in _table.IndexPathsForSelectedRows ?? []) _table.DeselectRow(path, false);
        }
        finally { _restoringSelection = false; }
        SyncSelectionFromTable();
    }

    #region Projection to table

    /// <summary>
    /// Coalesces projection changes (the Mac throttle): synchronization publishes many small batches, so the first
    /// change after a quiet period applies on the next main-queue turn and later ones in a burst wait for the window.
    /// The projection may raise from any thread, hence the interlocked flag.
    /// </summary>
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
            ApplyGroups(forceReload: false);
        });
    }

    private List<ListGroup> BuildGroups()
    {
        var groups = new List<ListGroup>();
        if (_projection is null) return groups;

        bool grouped = (ViewModel.MailListOptions?.GroupMode ?? MailListGroupMode.Date) != MailListGroupMode.None;
        foreach (var group in _projection.Groups)
        {
            if (group.Count == 0) continue;
            var title = grouped ? MailRowMapper.GroupTitle(group.Key) : string.Empty;
            groups.Add(new ListGroup(title ?? string.Empty, group.ToArray()));
        }
        return groups;
    }

    /// <summary>Rebuilds every row; used when the row appearance changes (preferences, theme, density).</summary>
    private void ReloadNow() => ApplyGroups(forceReload: true);

    private void ApplyGroups(bool forceReload)
    {
        if (_projection is null) return;

        var previousFirst = _table.IndexPathsForSelectedRows?.OrderBy(static path => path.Section).ThenBy(static path => path.Row).FirstOrDefault();
        int previousFlat = previousFirst is null ? -1 : FlatIndex(previousFirst);
        int previousRowCount = _lastRowCount;
        int previousSelectionCount = _selectionKeys.Count;
        var next = BuildGroups();
        bool sameShape = !forceReload && SameShape(next);
        _groups.Clear();
        _groups.AddRange(next);
        _lastRowCount = _groups.Sum(static group => group.Rows.Length);

        _restoringSelection = true;
        try
        {
            if (sameShape) RefreshVisibleCells();
            else _table.ReloadData();

            var selected = new List<NSIndexPath>();
            for (int section = 0; section < _groups.Count; section++)
                for (int row = 0; row < _groups[section].Rows.Length; row++)
                    if (_selectionKeys.Contains(SelectionKey(_groups[section].Rows[row]))) selected.Add(NSIndexPath.FromRowSection(row, section));

            // Removing the shown message selects its neighbour when the preference asks for it.
            bool smallRemoval = previousRowCount - _lastRowCount is > 0 && previousRowCount - _lastRowCount <= previousSelectionCount + 1;
            if (selected.Count == 0 && previousSelectionCount > 0 && previousFlat >= 0 && smallRemoval
                && _preferences.AutoSelectNextItem && !ViewModel.IsInitializingFolder && !_selectionMode && _lastRowCount > 0
                && PathAt(Math.Min(previousFlat, _lastRowCount - 1)) is { } neighbour)
                selected.Add(neighbour);

            foreach (var path in _table.IndexPathsForSelectedRows ?? [])
                if (!selected.Any(candidate => candidate.Section == path.Section && candidate.Row == path.Row)) _table.DeselectRow(path, false);
            foreach (var path in selected) _table.SelectRow(path, false, UITableViewScrollPosition.None);
        }
        finally { _restoringSelection = false; }

        SyncSelectionFromTable();
        UpdateOverlays();
        CheckLoadMore();
    }

    /// <summary>True when the groups and their rows are the ones shown, so the visible cells only need rebinding.</summary>
    private bool SameShape(List<ListGroup> next)
    {
        if (next.Count != _groups.Count || _table.NumberOfSections() != _groups.Count) return false;
        for (int index = 0; index < next.Count; index++)
        {
            if (next[index].Title != _groups[index].Title || next[index].Rows.Length != _groups[index].Rows.Length) return false;
            for (int row = 0; row < next[index].Rows.Length; row++)
                if (!ReferenceEquals(next[index].Rows[row], _groups[index].Rows[row])) return false;
        }
        return true;
    }

    /// <summary>Rebinds the realized mail cells so thread heads pick up new leaves and counts.</summary>
    private void RefreshVisibleCells()
    {
        bool heightsChanged = false;
        foreach (var path in _table.IndexPathsForVisibleRows ?? [])
        {
            if (RowAt(path) is not { } row || _table.CellAt(path) is not WinoMailRowCell cell) continue;
            var before = cell.Model;
            BindCell(cell, row);
            heightsChanged |= TileLayoutChanged(before, cell.Model);
        }
        if (heightsChanged) NoteHeightsChanged();
    }

    /// <summary>True when the row's tile line appeared, disappeared or got other tiles (which can wrap differently).</summary>
    private static bool TileLayoutChanged(WinoMailRowModel before, WinoMailRowModel after)
        => before.HasTileLine != after.HasTileLine
            || (after.HasTileLine && !WinoMailRowMeasure.SameTileLayout(before.Tiles, after.Tiles));

    /// <summary>Re-measures rows without the default height animation.</summary>
    private void NoteHeightsChanged() => UIView.PerformWithoutAnimation(() =>
    {
        _table.BeginUpdates();
        _table.EndUpdates();
    });

    /// <summary>The tile row wraps by width, so rows with tiles change height when the list or the select mode changes.</summary>
    private void ScheduleTileRelayout(bool force = false)
    {
        if (!_listBound || Density == WinoMailRowDensity.Compact) return;
        double width = (double)_table.Bounds.Width;
        if (!force && Math.Abs(width - _tileLayoutWidth) < 0.5) return;
        _tileLayoutWidth = width;
        if (_groups.Any(static group => group.Rows.Any(MailRowMapper.HasTiles))) NoteHeightsChanged();
    }

    public override void ViewDidLayoutSubviews()
    {
        base.ViewDidLayoutSubviews();
        ScheduleTileRelayout();
    }

    private static string SelectionKey(MailListRow row)
        => row.IsThreadHead ? "T:" + row.ThreadKey : "I:" + row.SourceItem.StableId.ToString("N");

    private MailListRow? RowAt(NSIndexPath path)
        => path.Section >= 0 && path.Section < _groups.Count && path.Row >= 0 && path.Row < _groups[path.Section].Rows.Length
            ? _groups[path.Section].Rows[path.Row]
            : null;

    private int FlatIndex(NSIndexPath path)
    {
        int index = 0;
        for (int section = 0; section < path.Section && section < _groups.Count; section++) index += _groups[section].Rows.Length;
        return index + path.Row;
    }

    private NSIndexPath? PathAt(int flat)
    {
        for (int section = 0; section < _groups.Count; section++)
        {
            if (flat < _groups[section].Rows.Length) return NSIndexPath.FromRowSection(flat, section);
            flat -= _groups[section].Rows.Length;
        }
        return null;
    }

    private NSIndexPath? IndexPathOf(Func<MailListRow, bool> match)
    {
        for (int section = 0; section < _groups.Count; section++)
            for (int row = 0; row < _groups[section].Rows.Length; row++)
                if (match(_groups[section].Rows[row])) return NSIndexPath.FromRowSection(row, section);
        return null;
    }

    private void SyncSelectionFromTable()
    {
        if (_projection is null) return;

        var selectedRows = new List<MailListRow>();
        foreach (var path in (_table.IndexPathsForSelectedRows ?? []).OrderBy(static path => path.Section).ThenBy(static path => path.Row))
            if (RowAt(path) is { } row) selectedRows.Add(row);

        _selectionKeys.Clear();
        foreach (var row in selectedRows) _selectionKeys.Add(SelectionKey(row));

        var result = MailListSelectionSnapshotBuilder.Build(selectedRows, _projection, _table.IndexPathForSelectedRow is { } current ? RowAt(current) : null);
        if (result.Signature == _lastPublishedSelection) return;
        _lastPublishedSelection = result.Signature;
        ViewModel.ApplyMailSelectionSnapshot(result.Snapshot);
    }

    private void ToggleThread(MailListRow row)
    {
        if (_projection is null || row.Thread is null) return;
        if (row.IsExpanded) _projection.CollapseThread(row.Thread.Key);
        else _projection.ExpandThread(row.Thread.Key);
    }

    private void CheckLoadMore()
    {
        if (!_listBound || _lastRowCount == 0) return;
        var offset = _table.ContentOffset.Y + _table.Bounds.Height;
        if (_table.ContentSize.Height - offset > 600) return;
        var command = ViewModel.LoadMoreItemsCommand;
        if (command.CanExecute(null)) Observe(command.ExecuteAsync(null));
    }

    #endregion

    #region Cells

    private WinoMailRowDensity Density => _densityOverride ?? MailRowMapper.Density(_preferences);

    private nfloat RowHeight(NSIndexPath path)
    {
        if (RowAt(path) is not { } row) return 0;
        var density = Density;
        if (density == WinoMailRowDensity.Compact || !MailRowMapper.HasTiles(row))
            return (nfloat)WinoMailRowModel.HeightFor(density, [], 0);

        var kind = row.Kind == MailListRowKind.ThreadChild ? WinoMailRowKind.ThreadChild : WinoMailRowKind.Single;
        _tileLayoutWidth = (double)_table.Bounds.Width;
        var textWidth = WinoMailRowMetrics.TextWidth(_tileLayoutWidth, density, kind, _preferences.IsShowSenderPicturesEnabled, _selectionMode);
        return (nfloat)WinoMailRowModel.HeightFor(density, MailRowMapper.Tiles(row), textWidth);
    }

    private UITableViewCell CreateCell(UITableView tableView, NSIndexPath path)
    {
        var cell = (WinoMailRowCell)tableView.DequeueReusableCell(WinoMailRowCell.ReuseIdentifier, path);
        if (RowAt(path) is not { } row) return cell;

        if (_wiredCells.Add(cell.Handle))
        {
            cell.SetLabels(Translator.MailOperation_Archive, Translator.MailOperation_Delete, Translator.MailOperation_SetFlag,
                Translator.MailOperation_ClearFlag, Translator.MailOperation_MarkAsRead, Translator.MailOperation_MarkAsUnread, Translator.MailOperation_MoveJunk);
            cell.HoverActionInvoked += (sender, kind) =>
            {
                if ((sender as WinoMailRowCell)?.Item is MailListRow hovered)
                    Observe(ViewModel.ExecuteHoverActionCommand.ExecuteAsync(new HoverActionCommandRequest(kind, hovered)));
            };
            cell.ThreadToggleRequested += (sender, _) =>
            {
                if ((sender as WinoMailRowCell)?.Item is MailListRow toggled) ToggleThread(toggled);
            };
        }
        BindCell(cell, row);
        return cell;
    }

    // Cells are reused; their event handlers are attached once per native cell.
    private readonly HashSet<ObjCRuntime.NativeHandle> _wiredCells = new();

    /// <summary>The row snapshot with the sender's Gravatar or favicon when one is cached.</summary>
    private WinoMailRowModel MapRow(MailListRow row)
    {
        var model = MailRowMapper.Map(row, _preferences, ViewModel.IsMergedAccountView, _densityOverride);
        return model.ShowPicture ? model with { Picture = _pictures.Get(model.SenderAddress) } : model;
    }

    /// <summary>A sender picture arrived or changed: re-apply the visible rows from that sender (all rows when null).</summary>
    private void SenderPictureChanged(object? sender, string? address) => OnUI(() =>
    {
        if (!_listBound) return;
        foreach (var path in _table.IndexPathsForVisibleRows ?? [])
        {
            if (RowAt(path) is not { } row || _table.CellAt(path) is not WinoMailRowCell cell) continue;
            if (address is not null && !string.Equals(cell.Model.SenderAddress?.Trim(), address, StringComparison.OrdinalIgnoreCase)) continue;
            cell.Apply(MapRow(row));
        }
        UpdateMultiSelectionOverlay();
    });

    private HoverActionSet ResolveHoverActions(MailListRow row)
    {
        if (ViewModel.MailActionResolver is { } resolver) return resolver.ResolveHoverActions(row);
        var defaults = MailListActionPreferences.GetDefaultHoverActions(_preferences);
        return new HoverActionSet(MailListActionResolver.ToHoverActionKind(defaults.Left),
            MailListActionResolver.ToHoverActionKind(defaults.Center), MailListActionResolver.ToHoverActionKind(defaults.Right));
    }

    private void RefreshHoverActions()
    {
        if (!_listBound) return;
        foreach (var path in _table.IndexPathsForVisibleRows ?? [])
        {
            if (RowAt(path) is { } row && _table.CellAt(path) is WinoMailRowCell cell)
                cell.HoverActions = ResolveHoverActions(row);
        }
    }

    private void BindCell(WinoMailRowCell cell, MailListRow row)
    {
        cell.Item = row;
        cell.IsSelectionMode = _selectionMode;
        cell.HoverActionsEnabled = _preferences.IsHoverActionsEnabled;
        cell.HoverActions = ResolveHoverActions(row);
        cell.Apply(MapRow(row));

        var leaves = (row.IsThreadHead ? row.LeafItems : [row.SourceItem]).OfType<INotifyPropertyChanged>().ToArray();
        var shown = cell.Model;
        // A synchronized mail raises many property changes in a row (and a thread head hears all of its leaves).
        // Coalesce them into one re-map per main-queue turn instead of one per property.
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
                // The thread gained or lost leaves: subscribe to the new set.
                if (rebind) BindCell(cell, row);
                else cell.Apply(MapRow(row));
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
        PropertyChangedEventHandler rowHandler = (_, args) =>
            Schedule(args.PropertyName is nameof(MailListRow.Thread) or nameof(MailListRow.LeafItems));
        row.PropertyChanged += rowHandler;
        cell.Subscription = new ActionDisposable(() =>
        {
            active = false;
            foreach (var leaf in leaves) leaf.PropertyChanged -= handler;
            row.PropertyChanged -= rowHandler;
        });
    }

    private void TableSelectionChanged()
    {
        if (_restoringSelection) return;
        SyncSelectionFromTable();
    }

    #endregion

    /// <summary>
    /// Debug bridge commands for screenshots: selectmail N… · mailinfo · mailop OPERATION · listbar · density
    /// compact|medium|spacious|auto · multiselect on|off.
    /// </summary>
    private void RegisterDebugCommands()
    {
#if DEBUG
        UIKitDebugBridge.Register("selectmail", args =>
        {
            if (_released) return Task.FromResult("released");
            var paths = (args.Length == 0 ? ["0"] : args).Select(arg => int.TryParse(arg, out var index) ? PathAt(index) : null).OfType<NSIndexPath>().ToArray();
            if (paths.Length == 0) return Task.FromResult("no such row");
            if (paths.Length > 1 && !_selectionMode) ViewModel.IsMultiSelectionModeEnabled = true;
            foreach (var path in paths) _table.SelectRow(path, false, UITableViewScrollPosition.Middle);
            SyncSelectionFromTable();
            return Task.FromResult("ok");
        });
        UIKitDebugBridge.Register("mailop", args =>
        {
            if (_released) return Task.FromResult("released");
            if (ViewModel.SelectedItemsCount == 0) return Task.FromResult("no selection");
            if (args.Length == 0 || !Enum.TryParse<MailOperation>(args[0], true, out var operation)) return Task.FromResult("usage: mailop OPERATION");
            RunSelectionOperation(operation);
            return Task.FromResult("ok");
        });
        UIKitDebugBridge.Register("mailinfo", _ => Task.FromResult(_released ? "released" :
            $"rows={_lastRowCount} groups={_groups.Count} selected={ViewModel.SelectedItemsCount} " +
            string.Join(" | ", ViewModel.SelectedItems.Take(3).Select(item => $"'{item.Subject}' read={item.IsRead} flagged={item.IsFlagged}"))));
        UIKitDebugBridge.Register("listbar", _ =>
        {
            ViewModel.BarSeverity = InfoBarMessageType.Success;
            ViewModel.BarTitle = "Inbox";
            ViewModel.BarMessage = "up to date";
            ViewModel.IsBarOpen = true;
            return Task.FromResult("ok");
        });
        UIKitDebugBridge.Register("density", args =>
        {
            _densityOverride = args.Length == 0 ? null : args[0].ToLowerInvariant() switch
            {
                "compact" => WinoMailRowDensity.Compact,
                "spacious" => WinoMailRowDensity.Spacious,
                "medium" => WinoMailRowDensity.Medium,
                _ => null
            };
            if (!_released) ReloadNow();
            return Task.FromResult("ok " + Density);
        });
        UIKitDebugBridge.Register("multiselect", args =>
        {
            ViewModel.IsMultiSelectionModeEnabled = args.Length == 0 || args[0].StartsWith("on", StringComparison.OrdinalIgnoreCase) || args[0] == "1";
            return Task.FromResult("ok");
        });
#endif
    }

    /// <summary>Data source and delegate of the mail table.</summary>
    private sealed class MailListTableSource(MailListPageViewController owner) : UITableViewSource
    {
        public override nint NumberOfSections(UITableView tableView) => owner._groups.Count;

        public override nint RowsInSection(UITableView tableview, nint section)
            => section >= 0 && section < owner._groups.Count ? owner._groups[(int)section].Rows.Length : 0;

        public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath) => owner.CreateCell(tableView, indexPath);

        public override nfloat GetHeightForRow(UITableView tableView, NSIndexPath indexPath) => owner.RowHeight(indexPath);

        public override nfloat GetHeightForHeader(UITableView tableView, nint section)
            => section < owner._groups.Count && owner._groups[(int)section].Title.Length > 0 ? (nfloat)WinoMailRowMeasure.GroupRowHeight : 0;

        /// <summary>Ungrouped lists get an empty header of height 0 (the binding's header is non-nullable).</summary>
        public override UIView GetViewForHeader(UITableView tableView, nint section)
        {
            var header = tableView.DequeueReusableHeaderFooterView(WinoMailGroupHeaderView.ReuseIdentifier) as WinoMailGroupHeaderView ?? new WinoMailGroupHeaderView();
            header.Title = section < owner._groups.Count ? owner._groups[(int)section].Title : string.Empty;
            header.Hidden = header.Title.Length == 0;
            header.RefreshTheme();
            return header;
        }

        /// <summary>Outside multi-select a conversation head expands or collapses; its messages open from their own rows.</summary>
        public override NSIndexPath? WillSelectRow(UITableView tableView, NSIndexPath indexPath)
        {
            if (owner._selectionMode || owner.RowAt(indexPath) is not { IsThreadHead: true } row) return indexPath;
            owner.ToggleThread(row);
            return null;
        }

        public override void RowSelected(UITableView tableView, NSIndexPath indexPath) => owner.TableSelectionChanged();

        public override void RowDeselected(UITableView tableView, NSIndexPath indexPath) => owner.TableSelectionChanged();

        public override void CellDisplayingEnded(UITableView tableView, UITableViewCell cell, NSIndexPath indexPath)
        {
            // Rows scrolled out go to the reuse queue: drop their item subscriptions now.
            if (cell is not WinoMailRowCell mail) return;
            mail.Subscription = null;
            mail.Item = null;
        }

        public override void Scrolled(UIScrollView scrollView) => owner.CheckLoadMore();

        public override UISwipeActionsConfiguration? GetLeadingSwipeActionsConfiguration(UITableView tableView, NSIndexPath indexPath)
            => owner.SwipeActions(indexPath, leading: true);

        public override UISwipeActionsConfiguration? GetTrailingSwipeActionsConfiguration(UITableView tableView, NSIndexPath indexPath)
            => owner.SwipeActions(indexPath, leading: false);

        public override UIContextMenuConfiguration? GetContextMenuConfiguration(UITableView tableView, NSIndexPath indexPath, CGPoint point)
            => owner.ContextMenu(indexPath);
    }
}
