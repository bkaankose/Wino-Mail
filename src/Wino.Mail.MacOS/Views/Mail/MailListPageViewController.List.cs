using System.ComponentModel;
using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Reader;
using Wino.Mail.Controls.AppKit.Common;
using Wino.Mail.Controls.AppKit.MailList;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Core.HoverActions;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Mail.ViewModels.Data;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

public sealed partial class MailListPageViewController
{
    /// <summary>A table row: either a date group header or a projected mail row.</summary>
    private sealed record ListEntry(MailListRow? Row, string Title);

    private readonly List<ListEntry> _entries = new();
    private readonly HashSet<string> _selectionKeys = new(StringComparer.Ordinal);
    private MailTableView _table = null!;
    private NSScrollView _scroll = null!;
    private MailListProjection? _projection;
    private MailListTableDataSource? _dataSource;
    private MailListTableDelegate? _delegate;
    private NSMenu? _contextMenu;
    private MailContextMenuDelegate? _contextMenuDelegate;
    private NSObject? _boundsObserver;
    private NSButton _selectAllCheckbox = null!;
    private WinoPivotBar _pivotBar = null!;
    private WinoIconView _onlineGlyph = null!;
    private NSButton _emptyFolderButton = null!;
    private NSButton _selectModeButton = null!;
    private NSPopUpButton _filterButton = null!;
    private NSMenu _filterMenu = null!;
    private WinoInfoBar _infoBar = null!;
    private WinoMailRowDensity? _densityOverride;
    private bool _syncingSelectAll;
    private NSView _onlineSearchPanel = null!;
    private NSTextField _emptyLabel = null!;
    private NSProgressIndicator _listProgress = null!;
    private static readonly TimeSpan ProjectionThrottle = TimeSpan.FromMilliseconds(120);
    private int _projectionApplyScheduled;
    private long _lastProjectionApply;
    private bool _restoringSelection;
    private bool _listBound;
    private bool _selectionMode;
    private string _lastPublishedSelection = string.Empty;
    private int _lastRowCount;

    private NSView BuildListPane()
    {
        var pane = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };

        // Header (Windows MailListPage "Pivot + Sync + Multi Select"): select-all checkbox, folder
        // pivot, online-search glyph, Empty folder, the multi-select toggle and the filter pull-down.
        _selectAllCheckbox = WinoCheckbox.Create(null, SelectAllToggled);
        _selectAllCheckbox.TranslatesAutoresizingMaskIntoConstraints = false;
        _selectAllCheckbox.ToolTip = Translator.Accessibility_SelectAllMessages;
        WinoAccessibility.Label(_selectAllCheckbox, Translator.Accessibility_SelectAllMessages);

        _pivotBar = new WinoPivotBar();
        _pivotBar.SelectionChanged += (_, index) => PivotClicked(index);

        _onlineGlyph = new WinoIconView(WinoIconGlyph.GlobeSearch, 16, WinoStyle.SecondaryText) { Hidden = true, ToolTip = Translator.SettingsAppPreferences_SearchMode_Online };

        _emptyFolderButton = GlyphButton(WinoIconGlyph.EmptyFolder, 18, Translator.FolderOperation_Empty, () => Observe(ViewModel.EmptyFolderCommand.ExecuteAsync(null)));
        _emptyFolderButton.Hidden = true;
        _selectModeButton = GlyphButton(WinoIconGlyph.MultiSelect, 16, Translator.Buttons_Multiselect, () => ViewModel.IsMultiSelectionModeEnabled = !ViewModel.IsMultiSelectionModeEnabled);
        _selectModeButton.SetButtonType(NSButtonType.PushOnPushOff);

        _filterMenu = new NSMenu { AutoEnablesItems = false };
        foreach (var option in ViewModel.FilterOptions)
        {
            var captured = option;
            _filterMenu.AddItem(new NSMenuItem(option.Title, (_, _) => Observe(ViewModel.SelectedFilterChangedCommand.ExecuteAsync(captured))));
        }
        _filterMenu.AddItem(NSMenuItem.SeparatorItem);
        foreach (var option in ViewModel.SortingOptions)
        {
            var captured = option;
            _filterMenu.AddItem(new NSMenuItem(option.Title, (_, _) => Observe(ViewModel.SelectedSortingChangedCommand.ExecuteAsync(captured))));
        }
        _filterButton = NSPopUpButton.CreatePullDownButton(ViewModel.SelectedFilterOption?.Title ?? string.Empty, WinoIcons.Image(WinoIconGlyph.Filter, 14), _filterMenu);
        _filterButton.TranslatesAutoresizingMaskIntoConstraints = false;
        _filterButton.ControlSize = NSControlSize.Regular;
        _filterButton.Font = WinoStyle.Body;
        _filterButton.ImagePosition = NSCellImagePosition.ImageLeading;
        _filterButton.ToolTip = Translator.Accessibility_FilterMessages;
        WinoAccessibility.Label(_filterButton, Translator.Accessibility_FilterMessages);
        _filterButton.SetContentCompressionResistancePriority(1000, NSLayoutConstraintOrientation.Horizontal);

        var header = WinoLayout.HStack(8, _selectAllCheckbox, _pivotBar, _onlineGlyph, _emptyFolderButton, _selectModeButton, _filterButton);
        header.Distribution = NSStackViewDistribution.Fill;
        header.SetCustomSpacing(4, _emptyFolderButton);
        header.SetCustomSpacing(4, _selectModeButton);
        header.EdgeInsets = new NSEdgeInsets(6, 10, 6, 6);
        header.SetClippingResistancePriority(200, NSLayoutConstraintOrientation.Horizontal);

        // Windows "Update Info Bar": IsClosable="False", DismissInterval="2", IsOpen bound two-way.
        _infoBar = new WinoInfoBar { Hidden = true, IsClosable = false, AutoDismissInterval = TimeSpan.FromSeconds(2) };
        _infoBar.Closed += (_, _) => ViewModel.IsBarOpen = false;
        var infoBarHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(_infoBar, infoBarHost, 4, 4, 6, 4);
        infoBarHost.Hidden = true;

        _table = new MailTableView(this)
        {
            HeaderView = null,
            Style = NSTableViewStyle.Plain,
            IntercellSpacing = new CGSize(0, 0),
            AllowsMultipleSelection = true,
            AllowsEmptySelection = true,
            AllowsTypeSelect = false,
            FloatsGroupRows = true,
            BackgroundColor = NSColor.Clear,
            ColumnAutoresizingStyle = NSTableViewColumnAutoresizingStyle.FirstColumnOnly,
            UsesAutomaticRowHeights = false
        };
        var column = new NSTableColumn("mail") { ResizingMask = NSTableColumnResizing.Autoresizing, Width = 380, MinWidth = 100, MaxWidth = 10000 };
        _table.AddColumn(column);
        WinoAccessibility.Label(_table, Translator.SettingsMessageList_Title);
        _dataSource = new MailListTableDataSource(this);
        _delegate = new MailListTableDelegate(this);
        _table.DataSource = _dataSource;
        _table.Delegate = _delegate;
        _contextMenuDelegate = new MailContextMenuDelegate(this);
        _contextMenu = new NSMenu { Delegate = _contextMenuDelegate, AutoEnablesItems = false };
        _table.Menu = _contextMenu;
        _table.DoubleClick += (_, _) => OpenClickedRow();

        _scroll = new NSScrollView
        {
            DocumentView = _table,
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            DrawsBackground = false,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _scroll.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        _scroll.ContentView.PostsBoundsChangedNotifications = true;

        _emptyLabel = WinoStyle.Label(Translator.NoMessageEmptyFolder, NSFont.SystemFontOfSize(20, NSFontWeight.UltraLight), WinoStyle.Informational.ColorWithAlphaComponent((nfloat)0.4), 0);
        _emptyLabel.Alignment = NSTextAlignment.Center;
        _emptyLabel.Hidden = true;
        _listProgress = new NSProgressIndicator { Style = NSProgressIndicatorStyle.Spinning, TranslatesAutoresizingMaskIntoConstraints = false, Hidden = true };
        var listHost = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(_scroll, listHost);
        listHost.AddSubview(_emptyLabel);
        listHost.AddSubview(_listProgress);
        listHost.SetContentHuggingPriorityForOrientation(1, NSLayoutConstraintOrientation.Vertical);
        NSLayoutConstraint.ActivateConstraints(
        [
            _emptyLabel.CenterXAnchor.ConstraintEqualTo(listHost.CenterXAnchor),
            _emptyLabel.CenterYAnchor.ConstraintEqualTo(listHost.CenterYAnchor),
            _emptyLabel.WidthAnchor.ConstraintLessThanOrEqualTo(listHost.WidthAnchor, 1, -40),
            _listProgress.CenterXAnchor.ConstraintEqualTo(listHost.CenterXAnchor),
            _listProgress.CenterYAnchor.ConstraintEqualTo(listHost.CenterYAnchor)
        ]);

        _onlineSearchPanel = BuildOnlineSearchPanel();
        _onlineSearchPanel.Hidden = true;

        var stack = new NSStackView
        {
            Orientation = NSUserInterfaceLayoutOrientation.Vertical,
            Spacing = 0,
            Alignment = NSLayoutAttribute.Leading,
            Distribution = NSStackViewDistribution.Fill,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        foreach (var view in new NSView[] { header, infoBarHost, BuildScopeBar(), listHost, _onlineSearchPanel })
        {
            stack.AddArrangedSubview(view);
            view.WidthAnchor.ConstraintEqualTo(stack.WidthAnchor).Active = true;
        }
        // Windows: the zone pads 5 on the sides and top; the rows carry the remaining 4pt gutter.
        WinoLayout.Fill(stack, pane, 5, 5, 0, 5);
        return pane;
    }

    private NSView BuildOnlineSearchPanel()
    {
        var host = new WinoSurfaceView { Fill = WinoStyle.SubtleFill, CornerRadius = WinoStyle.GroupRadius };
        var glyph = new WinoIconView(WinoIconGlyph.GlobeSearch, 18, WinoStyle.Accent);
        var text = WinoLayout.VStack(2,
            WinoStyle.Label(Translator.OnlineSearchTry_Line1, WinoStyle.BodyStrong),
            WinoStyle.Label(Translator.OnlineSearchTry_Line2, WinoStyle.Caption, WinoStyle.SecondaryText));
        var row = WinoLayout.HStack(10, glyph, text);
        row.EdgeInsets = new NSEdgeInsets(10, 12, 10, 12);
        WinoLayout.Fill(row, host);
        var button = new NSButton { Bordered = false, Title = string.Empty, Transparent = true, TranslatesAutoresizingMaskIntoConstraints = false };
        button.Activated += (_, _) => Observe(RunOnlineSearchAsync());
        WinoLayout.Fill(button, host);
        WinoAccessibility.Label(button, $"{Translator.OnlineSearchTry_Line1} {Translator.OnlineSearchTry_Line2}");
        var outer = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Fill(host, outer, 8, 12, 12, 12);
        return outer;
    }

    private NSButton GlyphButton(WinoIconGlyph glyph, double size, string label, Action action)
    {
        var button = new NSButton
        {
            Bordered = true,
            BezelStyle = NSBezelStyle.Recessed,
            ShowsBorderOnlyWhileMouseInside = true,
            Title = string.Empty,
            Image = WinoIcons.Image(glyph, size),
            ImagePosition = NSCellImagePosition.ImageOnly,
            ContentTintColor = WinoStyle.PrimaryText,
            ToolTip = label,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        button.SetButtonType(NSButtonType.MomentaryPushIn);
        WinoAccessibility.Label(button, label);
        WinoLayout.Size(button, 32, 30);
        button.Activated += (_, _) => action();
        return button;
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
            UpdateSelectAllCheckbox();
            UpdateMultiSelectionOverlay();
            CommandStateChanged?.Invoke(this, EventArgs.Empty);
        });

        _preferences.PreferenceChanged += PreferenceChanged;
        WinoStyle.AccentChanged += AccentChanged;
        WinoStyle.BackdropChanged += AccentChanged;
        RegisterDebugCommands();
        _boundsObserver = NSNotificationCenter.DefaultCenter.AddObserver(NSView.BoundsChangedNotification, _ => CheckLoadMore(), _scroll.ContentView);
    }

    private void ReleaseList()
    {
        if (!_listBound) return;
        _listBound = false;
        _preferences.PreferenceChanged -= PreferenceChanged;
        WinoStyle.AccentChanged -= AccentChanged;
        WinoStyle.BackdropChanged -= AccentChanged;
        ViewModel.PivotFolders.CollectionChanged -= PivotFoldersChanged;
        if (_boundsObserver is not null)
        {
            NSNotificationCenter.DefaultCenter.RemoveObserver(_boundsObserver);
            _boundsObserver = null;
        }
        if (_projection is not null)
        {
            _projection.ProjectionChanged -= ProjectionChanged;
            _projection.Dispose();
            _projection = null;
        }
        _entries.Clear();
        if (_table is not null)
        {
            _table.EnumerateAvailableRowViews((rowView, _) =>
            {
                if (rowView.NumberOfColumns > 0 && rowView.ViewAtColumn(0) is WinoMailRowView cell) cell.Subscription = null;
            });
            _table.Menu = null;
            _table.DataSource = null!;
            _table.Delegate = null!;
            _table.ReloadData();
        }
        _contextMenuDelegate?.Dispose();
        _contextMenuDelegate = null;
        _dataSource?.Dispose();
        _dataSource = null;
        _delegate?.Dispose();
        _delegate = null;
    }

    private void PreferenceChanged(object? sender, string name)
        => OnUI(() =>
        {
            if (name is nameof(_preferences.MailItemDisplayMode) or nameof(_preferences.IsShowPreviewEnabled)
                or nameof(_preferences.IsShowSenderPicturesEnabled) or nameof(_preferences.IsHoverActionsEnabled)
                or nameof(_preferences.AccountNicknamePosition))
                ReloadNow();
        });

    private void AccentChanged(object? sender, EventArgs args) => OnUI(() => { if (_listBound) ReloadNow(); });

    private void PivotFoldersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args) => OnUI(UpdatePivots);

    private void UpdatePivots()
    {
        var pivots = ViewModel.PivotFolders.ToArray();
        var items = pivots.Select(static pivot => new WinoPivotItem(pivot.FolderTitle, pivot.ShouldDisplaySelectedItemCount ? pivot.SelectedItemCount : 0)).ToArray();
        _pivotBar.SetItems(items, Array.IndexOf(pivots, ViewModel.SelectedFolderPivot));
    }

    /// <summary>Windows FolderPivotChanged: flag the pivots, clear the selection and run the pivot command.</summary>
    private void PivotClicked(int index)
    {
        var pivots = ViewModel.PivotFolders.ToArray();
        if (index < 0 || index >= pivots.Length || ReferenceEquals(pivots[index], ViewModel.SelectedFolderPivot)) return;
        foreach (var pivot in pivots) pivot.IsSelected = false;
        pivots[index].IsSelected = true;
        ViewModel.IsMultiSelectionModeEnabled = false;
        _table.DeselectAll(null);
        ViewModel.SelectedFolderPivot = pivots[index];
        _pivotBar.SelectedIndex = index;
        Observe(ViewModel.SelectedPivotChangedCommand.ExecuteAsync(pivots[index]));
    }

    private void UpdateFilterMenu()
    {
        _filterButton.Title = ViewModel.SelectedFilterOption?.Title ?? string.Empty;
        var items = _filterMenu.Items;
        int filters = ViewModel.FilterOptions.Count;
        for (int index = 0; index < filters && index < items.Length; index++)
            items[index].State = ReferenceEquals(ViewModel.FilterOptions[index], ViewModel.SelectedFilterOption) ? NSCellStateValue.On : NSCellStateValue.Off;
        for (int index = 0; index < ViewModel.SortingOptions.Count && filters + 1 + index < items.Length; index++)
            items[filters + 1 + index].State = ReferenceEquals(ViewModel.SortingOptions[index], ViewModel.SelectedSortingOption) ? NSCellStateValue.On : NSCellStateValue.Off;
    }

    private void SelectAllToggled()
    {
        if (_syncingSelectAll) return;
        if (_selectAllCheckbox.State == NSCellStateValue.On) SelectAllRows();
        else _table.DeselectAll(null);
    }

    private void UpdateSelectAllCheckbox()
    {
        _syncingSelectAll = true;
        _selectAllCheckbox.State = ViewModel.IsAllItemsSelected ? NSCellStateValue.On : NSCellStateValue.Off;
        _syncingSelectAll = false;
    }

    private void UpdateInfoBar()
    {
        bool open = ViewModel.IsBarOpen && !string.IsNullOrWhiteSpace(ViewModel.BarMessage);
        _infoBar.Title = ViewModel.BarTitle;
        _infoBar.Message = ViewModel.BarMessage;
        _infoBar.Severity = ViewModel.BarSeverity switch
        {
            InfoBarMessageType.Success => WinoInfoBarSeverity.Success,
            InfoBarMessageType.Warning => WinoInfoBarSeverity.Warning,
            InfoBarMessageType.Error => WinoInfoBarSeverity.Error,
            _ => WinoInfoBarSeverity.Informational
        };
        _infoBar.Hidden = !open;
        if (_infoBar.Superview is { } host) host.Hidden = !open;
    }

    private void UpdateOverlays()
    {
        bool empty = _entries.Count == 0;
        bool loading = ViewModel.IsInitializingFolder && empty;
        _listProgress.Hidden = !loading;
        if (loading) _listProgress.StartAnimation(null);
        else _listProgress.StopAnimation(null);
        _emptyLabel.StringValue = ViewModel.IsInSearchMode ? Translator.NoMessageCrieteria : Translator.NoMessageEmptyFolder;
        _emptyLabel.Hidden = !(empty && !ViewModel.IsInitializingFolder);
    }

    private void ApplySelectionMode(bool enabled)
    {
        _selectionMode = enabled;
        _selectModeButton.State = enabled ? NSCellStateValue.On : NSCellStateValue.Off;
        _selectModeButton.ContentTintColor = enabled ? WinoStyle.Accent : WinoStyle.PrimaryText;
        _table.EnumerateAvailableRowViews((rowView, _) =>
        {
            if (rowView is WinoMailTableRowView mailRow)
            {
                mailRow.IsSelectionMode = enabled;
                mailRow.NeedsDisplay = true;
                if (mailRow.NumberOfColumns > 0 && mailRow.ViewAtColumn(0) is WinoMailRowView cell)
                {
                    cell.IsSelectionMode = enabled;
                    cell.BackgroundStyle = mailRow.InteriorBackgroundStyle;
                }
            }
        });
    }

    private void SelectAllRows()
    {
        var indexes = new NSMutableIndexSet();
        for (int index = 0; index < _entries.Count; index++)
            if (_entries[index].Row is not null) indexes.Add((nuint)index);
        _table.SelectRows(indexes, false);
    }

    // ---- Projection to table ----

    /// <summary>
    /// Coalesces projection changes. Synchronization publishes many small batches; each one used to
    /// rebuild every entry and reload the whole table. The first change after a quiet period applies
    /// on the next run-loop turn, later ones in a burst wait until the throttle window has passed,
    /// so a burst costs a handful of table updates instead of one per batch.
    /// The projection may raise from any thread, hence the interlocked flag.
    /// </summary>
    private void ProjectionChanged(object? sender, EventArgs args)
    {
        if (_released || Interlocked.Exchange(ref _projectionApplyScheduled, 1) == 1) return;
        var sinceLast = System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastProjectionApply));
        var delay = sinceLast >= ProjectionThrottle ? TimeSpan.Zero : ProjectionThrottle - sinceLast;
        CoreFoundation.DispatchQueue.MainQueue.DispatchAfter(new CoreFoundation.DispatchTime(CoreFoundation.DispatchTime.Now, delay), () =>
        {
            Interlocked.Exchange(ref _projectionApplyScheduled, 0);
            if (_released || !_listBound) return;
            Interlocked.Exchange(ref _lastProjectionApply, System.Diagnostics.Stopwatch.GetTimestamp());
            ApplyEntries(forceReload: false);
        });
    }

    private void RebuildEntries()
    {
        _entries.Clear();
        if (_projection is null) return;
        bool grouped = (ViewModel.MailListOptions?.GroupMode ?? MailListGroupMode.Date) != MailListGroupMode.None;
        foreach (var group in _projection.Groups)
        {
            if (group.Count == 0) continue;
            if (grouped)
            {
                var title = MailRowMapper.GroupTitle(group.Key);
                if (!string.IsNullOrEmpty(title)) _entries.Add(new ListEntry(null, title));
            }
            foreach (var row in group) _entries.Add(new ListEntry(row, string.Empty));
        }
    }

    /// <summary>Rebuilds every row; used when the row appearance changes (preferences, theme, density).</summary>
    private void ReloadNow() => ApplyEntries(forceReload: true);

    private void ApplyEntries(bool forceReload)
    {
        if (_projection is null) return;
        int previousFirst = _table.SelectedRows.Count > 0 ? (int)_table.SelectedRows.FirstIndex : -1;
        int previousRowCount = _lastRowCount;
        int previousSelectionCount = _selectionKeys.Count;
        var previousEntries = forceReload ? null : _entries.ToArray();
        RebuildEntries();
        _lastRowCount = _entries.Count(static entry => entry.Row is not null);

        _restoringSelection = true;
        try
        {
            if (previousEntries is null || !TryApplyIncrementally(previousEntries)) _table.ReloadData();
            var indexes = new NSMutableIndexSet();
            for (int index = 0; index < _entries.Count; index++)
                if (_entries[index].Row is { } row && _selectionKeys.Contains(SelectionKey(row))) indexes.Add((nuint)index);

            // Removing the shown message selects its neighbour when the preference asks for it.
            bool smallRemoval = previousRowCount - _lastRowCount is > 0 && previousRowCount - _lastRowCount <= previousSelectionCount + 1;
            if (indexes.Count == 0 && previousSelectionCount > 0 && previousFirst >= 0 && smallRemoval
                && _preferences.AutoSelectNextItem && !ViewModel.IsInitializingFolder && !_selectionMode)
            {
                int candidate = Math.Min(previousFirst, _entries.Count - 1);
                while (candidate >= 0 && candidate < _entries.Count && _entries[candidate].Row is null) candidate++;
                if (candidate >= _entries.Count) candidate = _entries.FindLastIndex(static entry => entry.Row is not null);
                if (candidate >= 0) indexes.Add((nuint)candidate);
            }
            _table.SelectRows(indexes, false);
        }
        finally { _restoringSelection = false; }

        SyncSelectionFromTable();
        UpdateOverlays();
        CheckLoadMore();
    }

    /// <summary>
    /// Applies a projection change as one batched insert or remove when the new entries only add
    /// to, or only drop from, the shown ones in the same order (the usual synchronization and
    /// delete cases). Existing row views stay realized, so the table neither re-measures every row
    /// nor rebinds every visible cell. Anything else (re-sorts, moves) falls back to a full reload.
    /// </summary>
    private bool TryApplyIncrementally(ListEntry[] previous)
    {
        if (previous.Length == 0 || _entries.Count == 0 || (nint)previous.Length != _table.RowCount) return false;
        if (previous.Length == _entries.Count)
        {
            for (int index = 0; index < previous.Length; index++)
                if (!Equals(previous[index], _entries[index])) return false;
            RefreshVisibleCells();
            return true;
        }

        bool inserting = _entries.Count > previous.Length;
        IReadOnlyList<ListEntry> longer = inserting ? _entries : previous;
        IReadOnlyList<ListEntry> shorter = inserting ? previous : _entries;
        var changed = new NSMutableIndexSet();
        int matched = 0;
        for (int index = 0; index < longer.Count; index++)
        {
            if (matched < shorter.Count && Equals(longer[index], shorter[matched])) matched++;
            else changed.Add((nuint)index);
        }
        if (matched != shorter.Count) return false;

        _table.BeginUpdates();
        try
        {
            if (inserting) _table.InsertRows(changed, NSTableViewAnimation.None);
            else _table.RemoveRows(changed, NSTableViewAnimation.None);
        }
        finally { _table.EndUpdates(); }
        RefreshVisibleCells();
        return true;
    }

    /// <summary>Rebinds the realized mail cells so thread heads pick up new leaves and counts.</summary>
    private void RefreshVisibleCells()
    {
        var visible = _table.RowsInRect(_table.VisibleRect());
        var heights = new NSMutableIndexSet();
        for (nint index = visible.Location; index < visible.Location + visible.Length; index++)
        {
            if (RowAt(index) is not { } row || _table.GetView(0, index, false) is not WinoMailRowView cell) continue;
            bool hadTiles = cell.Model.HasTileLine;
            BindCell(cell, row);
            if (cell.Model.HasTileLine != hadTiles) heights.Add((nuint)index);
        }
        if (heights.Count > 0) _table.NoteHeightOfRowsWithIndexesChanged(heights);
    }

    private static string SelectionKey(MailListRow row)
        => row.IsThreadHead ? "T:" + row.ThreadKey : "I:" + row.SourceItem.StableId.ToString("N");

    private MailListRow? RowAt(nint index)
        => index >= 0 && index < _entries.Count ? _entries[(int)index].Row : null;

    private void SyncSelectionFromTable()
    {
        if (_projection is null) return;
        var selectedRows = new List<MailListRow>();
        foreach (var index in _table.SelectedRows.ToArray())
            if (RowAt((nint)index) is { } row) selectedRows.Add(row);

        _selectionKeys.Clear();
        foreach (var row in selectedRows) _selectionKeys.Add(SelectionKey(row));

        var items = new List<IMailListSourceItem>();
        var ids = new HashSet<Guid>();
        var fullThreads = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in selectedRows)
        {
            if (row.IsThreadHead && row.Thread is { } thread)
            {
                fullThreads.Add(thread.Key);
                foreach (var leaf in thread.Items)
                    if (ids.Add(leaf.StableId)) items.Add(leaf);
            }
            else if (ids.Add(row.SourceItem.StableId)) items.Add(row.SourceItem);
        }
        foreach (var thread in _projection.Threads)
            if (thread.Items.Count > 0 && thread.Items.All(leaf => ids.Contains(leaf.StableId))) fullThreads.Add(thread.Key);

        var active = RowAt(_table.SelectedRow)?.SourceItem ?? selectedRows.LastOrDefault()?.SourceItem;
        var signature = string.Join(",", ids.OrderBy(static id => id)) + "|" + active?.StableId;
        if (signature == _lastPublishedSelection) return;
        _lastPublishedSelection = signature;
        ViewModel.ApplyMailSelectionSnapshot(new MailListSelectionSnapshot(items, fullThreads, active));
    }

    private void ToggleThread(MailListRow row)
    {
        if (_projection is null || row.Thread is null) return;
        if (row.IsExpanded) _projection.CollapseThread(row.Thread.Key);
        else _projection.ExpandThread(row.Thread.Key);
    }

    private void OpenClickedRow()
    {
        if (RowAt(_table.ClickedRow) is { IsThreadHead: true } row) ToggleThread(row);
    }

    private void CheckLoadMore()
    {
        if (!_listBound || _entries.Count == 0) return;
        var visible = _scroll.ContentView.Bounds;
        var documentHeight = _table.Frame.Height;
        if (documentHeight - (visible.Y + visible.Height) > 600) return;
        var command = ViewModel.LoadMoreItemsCommand;
        if (command.CanExecute(null)) Observe(command.ExecuteAsync(null));
    }

    // ---- Cells ----

    private WinoMailRowDensity Density => _densityOverride ?? MailRowMapper.Density(_preferences);

    private nfloat RowHeight(nint index)
    {
        if (RowAt(index) is not { } row) return (nfloat)WinoMailRowMetrics.GroupRowHeight;
        return (nfloat)WinoMailRowModel.HeightFor(Density, MailRowMapper.HasTiles(row));
    }

    private NSView CreateCell(NSTableView tableView, nint index)
    {
        var entry = _entries[(int)index];
        if (entry.Row is null)
        {
            var header = tableView.MakeView(WinoMailGroupHeaderView.ReuseIdentifier, this) as WinoMailGroupHeaderView ?? new WinoMailGroupHeaderView();
            header.Title = entry.Title;
            header.RefreshTheme();
            return header;
        }

        if (tableView.MakeView(WinoMailRowView.ReuseIdentifier, this) is not WinoMailRowView cell)
        {
            cell = new WinoMailRowView();
            cell.SetLabels(Translator.MailOperation_Archive, Translator.MailOperation_Delete, Translator.MailOperation_SetFlag,
                Translator.MailOperation_ClearFlag, Translator.MailOperation_MarkAsRead, Translator.MailOperation_MarkAsUnread);
            cell.HoverActionInvoked += (sender, kind) =>
            {
                if ((sender as WinoMailRowView)?.Item is MailListRow hovered)
                    Observe(ViewModel.ExecuteHoverActionCommand.ExecuteAsync(new HoverActionCommandRequest(kind, hovered)));
            };
            cell.ThreadToggleRequested += (sender, _) =>
            {
                if ((sender as WinoMailRowView)?.Item is MailListRow toggled) ToggleThread(toggled);
            };
            cell.CheckboxToggled += (sender, _) =>
            {
                if (sender is not WinoMailRowView toggledCell) return;
                nint row = _table.RowForView(toggledCell);
                if (row < 0) return;
                if (_table.IsRowSelected(row)) _table.DeselectRow(row);
                else _table.SelectRow(row, true);
            };
        }

        BindCell(cell, entry.Row);
        return cell;
    }

    private void BindCell(WinoMailRowView cell, MailListRow row)
    {
        cell.Item = row;
        cell.IsSelectionMode = _selectionMode;
        cell.HoverActionsEnabled = _preferences.IsHoverActionsEnabled;
        cell.Apply(MailRowMapper.Map(row, _preferences, ViewModel.IsMergedAccountView, _densityOverride));

        var leaves = (row.IsThreadHead ? row.LeafItems : [row.SourceItem]).OfType<INotifyPropertyChanged>().ToArray();
        bool hadTiles = cell.Model.HasTileLine;
        // A synchronized mail raises many property changes in a row (and a thread head hears all of
        // its leaves). Coalesce them into one re-map per run-loop turn instead of one per property.
        int pending = 0;
        bool rebind = false;
        bool active = true;
        void Schedule(bool leavesChanged)
        {
            if (leavesChanged) rebind = true;
            if (Interlocked.Exchange(ref pending, 1) == 1) return;
            NSApplication.SharedApplication.BeginInvokeOnMainThread(() =>
            {
                Interlocked.Exchange(ref pending, 0);
                if (!active || _released || !ReferenceEquals(cell.Item, row)) return;
                if (rebind)
                {
                    // The thread gained or lost leaves: subscribe to the new set.
                    BindCell(cell, row);
                    hadTiles = cell.Model.HasTileLine;
                }
                else cell.Apply(MailRowMapper.Map(row, _preferences, ViewModel.IsMergedAccountView, _densityOverride));
                if (cell.Model.HasTileLine != hadTiles)
                {
                    hadTiles = cell.Model.HasTileLine;
                    nint index = _table.RowForView(cell);
                    if (index >= 0) _table.NoteHeightOfRowsWithIndexesChanged(new NSIndexSet((nuint)index));
                }
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

    private NSTableRowView CreateRowView(NSTableView tableView, nint index)
    {
        if (RowAt(index) is null)
            return tableView.MakeView(WinoMailGroupRowView.ReuseIdentifier, this) as WinoMailGroupRowView ?? new WinoMailGroupRowView();
        var rowView = tableView.MakeView(WinoMailTableRowView.ReuseIdentifier, this) as WinoMailTableRowView ?? new WinoMailTableRowView();
        rowView.IsSelectionMode = _selectionMode;
        return rowView;
    }

    /// <summary>In select mode a plain click toggles the clicked row instead of replacing the selection.</summary>
    private NSIndexSet ProposeSelection(NSIndexSet proposed)
    {
        var filtered = new NSMutableIndexSet();
        foreach (var index in proposed.ToArray())
            if (RowAt((nint)index) is not null) filtered.Add(index);

        if (!_selectionMode || filtered.Count != 1) return filtered;
        var current = NSApplication.SharedApplication.CurrentEvent;
        if (current is null || current.Type is not (NSEventType.LeftMouseDown or NSEventType.LeftMouseUp)) return filtered;
        if ((current.ModifierFlags & (NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ShiftKeyMask)) != 0) return filtered;

        var toggled = new NSMutableIndexSet(_table.SelectedRows);
        var clicked = filtered.FirstIndex;
        if (toggled.Contains(clicked)) toggled.Remove(clicked);
        else toggled.Add(clicked);
        return toggled;
    }

    private void TableSelectionChanged()
    {
        if (_restoringSelection) return;
        SyncSelectionFromTable();
    }

    /// <summary>
    /// Debug bridge commands for screenshots: selectmail N · density compact|medium|spacious|auto ·
    /// multiselect on|off · hover N|off · listwidth W.
    /// </summary>
    private void RegisterDebugCommands()
    {
#if DEBUG
        int MailRowIndex(int ordinal)
        {
            int seen = -1;
            for (int index = 0; index < _entries.Count; index++)
                if (_entries[index].Row is not null && ++seen == ordinal) return index;
            return -1;
        }
        MacDebugBridge.Register("selectmail", args =>
        {
            if (_released) return Task.FromResult("released");
            var indexes = new NSMutableIndexSet();
            foreach (var arg in args.Length == 0 ? ["0"] : args)
            {
                int index = MailRowIndex(int.Parse(arg));
                if (index >= 0) indexes.Add((nuint)index);
            }
            if (indexes.Count == 0) return Task.FromResult("no such row");
            _table.SelectRows(indexes, false);
            _table.ScrollRowToVisible((nint)indexes.FirstIndex);
            return Task.FromResult("ok");
        });
        // "mailop OPERATION" runs a MailOperation on the selection, as the reading-pane bar does.
        MacDebugBridge.Register("mailop", args =>
        {
            if (_released) return Task.FromResult("released");
            if (ViewModel.SelectedItemsCount == 0) return Task.FromResult("no selection");
            RunSelectionOperation(Enum.Parse<MailOperation>(args[0], true));
            return Task.FromResult("ok");
        });
        // "mailinfo" reports the selection's state and the list size.
        MacDebugBridge.Register("mailinfo", _ => Task.FromResult(_released ? "released" :
            $"rows={_entries.Count(entry => entry.Row is not null)} selected={ViewModel.SelectedItemsCount} " +
            string.Join(" | ", ViewModel.SelectedItems.Take(3).Select(item => $"'{item.Subject}' read={item.IsRead} flagged={item.IsFlagged} folder={item.MailCopy.FolderId}"))));
        MacDebugBridge.Register("density", args =>
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
        MacDebugBridge.Register("multiselect", args =>
        {
            ViewModel.IsMultiSelectionModeEnabled = args.Length == 0 || args[0].StartsWith("on", StringComparison.OrdinalIgnoreCase) || args[0] == "1";
            return Task.FromResult("ok");
        });
        MacDebugBridge.Register("hover", args =>
        {
            _table.EnumerateAvailableRowViews((rowView, _) => (rowView as WinoMailTableRowView)?.SetHovered(false));
            if (args.Length > 0 && !args[0].StartsWith("off", StringComparison.OrdinalIgnoreCase))
            {
                int index = MailRowIndex(int.Parse(args[0]));
                if (index >= 0 && _table.GetRowView(index, false) is WinoMailTableRowView row) row.SetHovered(true);
            }
            return Task.FromResult("ok");
        });
        MacDebugBridge.Register("listwidth", args => { SetListWidth(double.Parse(args[0])); return Task.FromResult("ok"); });
        MacDebugBridge.Register("snapzone", args =>
        {
            // Renders one zone on its own for close inspection: snapzone list|reader NAME.
            var view = args.Length > 0 && args[0].StartsWith("r", StringComparison.OrdinalIgnoreCase) ? (NSView)_readerZone : _listZone;
            view.LayoutSubtreeIfNeeded();
            var rep = view.BitmapImageRepForCachingDisplayInRect(view.Bounds);
            if (rep is null) return Task.FromResult("no rep");
            view.CacheDisplay(view.Bounds, rep);
            using var data = rep.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png, new NSDictionary());
            var file = Path.Combine(Path.GetTempPath(), "wino-debug", (args.Length > 1 ? args[1] : "zone") + ".png");
            data?.Save(file, true);
            return Task.FromResult(Path.GetFileName(file));
        });
        MacDebugBridge.Register("dismiss", _ =>
        {
            int closed = 0;
            foreach (var window in NSApplication.SharedApplication.DangerousWindows.ToArray())
            {
                if (!window.IsVisible || window is not NSPanel) continue;
                if (window.SheetParent is { } parent) parent.EndSheet(window);
                else window.Close();
                closed++;
            }
            if (NSApplication.SharedApplication.ModalWindow is not null) NSApplication.SharedApplication.StopModal();
            return Task.FromResult("closed " + closed);
        });
#endif
    }

    private sealed class MailListTableDataSource(MailListPageViewController owner) : NSTableViewDataSource
    {
        public override nint GetRowCount(NSTableView tableView) => owner._entries.Count;
    }

    private sealed class MailListTableDelegate(MailListPageViewController owner) : NSTableViewDelegate
    {
        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn? tableColumn, nint row) => owner.CreateCell(tableView, row);
        public override NSTableRowView CoreGetRowView(NSTableView tableView, nint row) => owner.CreateRowView(tableView, row);
        public override nfloat GetRowHeight(NSTableView tableView, nint row) => owner.RowHeight(row);
        public override bool IsGroupRow(NSTableView tableView, nint row) => owner.RowAt(row) is null;
        public override bool ShouldSelectRow(NSTableView tableView, nint row) => owner.RowAt(row) is not null;
        public override NSIndexSet GetSelectionIndexes(NSTableView tableView, NSIndexSet proposedSelectionIndexes) => owner.ProposeSelection(proposedSelectionIndexes);
        public override void SelectionDidChange(NSNotification notification) => owner.TableSelectionChanged();
        public override NSTableViewRowAction[] RowActions(NSTableView tableView, nint row, NSTableRowActionEdge edge) => owner.SwipeActions(row, edge);

        /// <summary>Rows scrolled out or reloaded go to the reuse queue: drop their item subscriptions now.</summary>
        public override void DidRemoveRowView(NSTableView tableView, NSTableRowView rowView, nint row)
        {
            if (rowView.NumberOfColumns > 0 && rowView.ViewAtColumn(0) is WinoMailRowView cell)
            {
                cell.Subscription = null;
                cell.Item = null;
            }
        }
    }

    /// <summary>
    /// Table subclass: keeps its one column as wide as the table so rows stretch to the trailing
    /// edge, and handles keys (arrows open and close threads, Delete removes the selection).
    /// </summary>
    private sealed class MailTableView(MailListPageViewController owner) : NSTableView
    {
        public override void Layout()
        {
            var columns = TableColumns();
            var width = EnclosingScrollView?.ContentView.Bounds.Width ?? Bounds.Width;
            if (columns.Length > 0 && width > 0 && Math.Abs(columns[0].Width - width) > 0.5) columns[0].Width = width;
            base.Layout();
        }

        public override void KeyDown(NSEvent theEvent)
        {
            var key = theEvent.CharactersIgnoringModifiers;
            if (!string.IsNullOrEmpty(key) && owner.RowAt(SelectedRow) is { } row)
            {
                char character = key[0];
                if (character == (char)NSFunctionKey.RightArrow && row.IsThreadHead && !row.IsExpanded) { owner.ToggleThread(row); return; }
                if (character == (char)NSFunctionKey.LeftArrow && row.IsThreadHead && row.IsExpanded) { owner.ToggleThread(row); return; }
                if (character is (char)127 or (char)NSFunctionKey.Delete) { owner.Execute(Wino.Mail.MacOS.Infrastructure.ShellCommand.Delete, null); return; }
            }
            base.KeyDown(theEvent);
        }

        public override NSMenu? MenuForEvent(NSEvent theEvent)
        {
            var point = ConvertPointFromView(theEvent.LocationInWindow, null);
            return GetRow(point) is var row && row >= 0 && owner.RowAt(row) is not null ? base.MenuForEvent(theEvent) : null;
        }
    }
}
