using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Reader;
using Wino.Mail.Controls.Linux.MailList;
using Wino.Mail.ViewModels;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The list header (pivots, select all, select mode, Empty folder, filter and sort) and the bars around the list
/// (folder sync notice, Other link, undo, update message): the port of macOS <c>UpdatePivots</c>, <c>PivotClicked</c>,
/// <c>UpdateFilterMenu</c> and <c>MailListPageViewController.Bars</c>.
/// </summary>
internal sealed partial class MailListView
{
    private MailListHeader? _header;
    private MailListBars? _bars;
    private MainLoop.Coalescer? _undoRefresh;
    private object? _syncFolder;

    /// <summary>ML-11 and ML-12 append their entries (search filters) to the filter and sort menu.</summary>
    partial void AppendFilterMenu(WinoMenuBuilder builder);

    partial void BuildHeader()
    {
        var self = _self;
        var header = _header = Bindings.Own(new MailListHeader());
        var bars = _bars = Bindings.Own(new MailListBars());
        HeaderSlot.Append(header.Root);
        HeaderSlot.Append(bars.Root);
        FloatingSlot.Append(bars.Floating);

        header.Pivots.SelectionChanged += index => self.Value?.PivotClicked(index);
        header.SelectAllToggled += all =>
        {
            if (self.Value is not { } view) return;
            if (all) view.SelectAllRows();
            else view.ClearSelection();
        };
        header.SelectionModeToggled += on =>
        {
            if (self.Value is { } view) view.ViewModel.IsMultiSelectionModeEnabled = on;
        };
        header.EmptyFolderRequested += () =>
        {
            if (self.Value is { } view && view.ViewModel.EmptyFolderCommand.CanExecute(null)) view.Observe(view.ViewModel.EmptyFolderCommand.ExecuteAsync(null));
        };
        header.FilterMenuRequested += anchor => self.Value?.ShowFilterMenu(anchor);
        bars.EnableSyncRequested += () =>
        {
            if (self.Value is { } view) view.Observe(view.ViewModel.EnableFolderSynchronizationCommand.ExecuteAsync(null));
        };
        bars.OtherInboxRequested += () =>
        {
            if (self.Value is { } view) view.Observe(view.ViewModel.OpenOtherInboxCommand.ExecuteAsync(null));
        };
        bars.UndoRequested += () =>
        {
            if (self.Value is not { } view) return;
            view._bars?.HideUndo();
            view.Observe(view.ViewModel.UndoLatestQueuedActionCommand.ExecuteAsync(null));
        };
        bars.UndoDismissed += () =>
        {
            if (self.Value is { } view) view.ViewModel.IsUndoMailActionBarOpen = false;
        };
        bars.UpdateDismissed += () =>
        {
            if (self.Value is { } view) view.ViewModel.IsBarOpen = false;
        };

        Bind.BindCollection(ViewModel.PivotFolders, UpdatePivots);
        Bind.Bind(nameof(MailListPageViewModel.SelectedFolderPivot), static vm => vm.SelectedFolderPivot, _ => UpdatePivots());
        Bind.Bind(nameof(MailListPageViewModel.ActiveFolder), static vm => vm.ActiveFolder, folder =>
        {
            UpdatePivots();
            if (!ReferenceEquals(folder, _syncFolder))
            {
                _syncFolder = folder;
                _bars?.ResetSyncDismissal();
            }
            UpdateSyncNotice();
        });
        // The ViewModel writes the pivot's count after it raised SelectedItemsCount.
        Bind.Bind(nameof(MailListPageViewModel.SelectedItemsCount), static vm => vm.SelectedItemsCount, _ => MainLoop.Post(() => self.Value?.UpdatePivots()));
        Bind.Bind(nameof(MailListPageViewModel.SelectedFilterOption), static vm => vm.SelectedFilterOption, option => _header?.SetFilterTitle(option?.Title ?? string.Empty));
        Bind.Bind(nameof(MailListPageViewModel.AreSearchResultsOnline), static vm => vm.AreSearchResultsOnline, online => _header?.SetOnlineResults(online));
        Bind.Bind(nameof(MailListPageViewModel.IsEmptyFolderButtonVisible), static vm => vm.IsEmptyFolderButtonVisible, visible => _header?.SetEmptyFolderVisible(visible));
        Bind.Bind(nameof(MailListPageViewModel.IsMultiSelectionModeEnabled), static vm => vm.IsMultiSelectionModeEnabled, on =>
        {
            _header?.SetSelectionMode(on);
            UpdateSelectAll();
        });
        Bind.Bind(nameof(MailListPageViewModel.IsSynchronizationDisabledNoticeVisible), static vm => vm.IsSynchronizationDisabledNoticeVisible, _ => UpdateSyncNotice());
        Bind.Bind(nameof(MailListPageViewModel.IsSyncButtonVisible), static vm => vm.IsSyncButtonVisible, _ => UpdateSyncNotice());
        Bind.Bind(nameof(MailListPageViewModel.IsOtherInboxUnreadNoticeVisible), static vm => vm.IsOtherInboxUnreadNoticeVisible, _ => UpdateOtherInbox());
        Bind.Bind(nameof(MailListPageViewModel.OtherInboxUnreadNoticeText), static vm => vm.OtherInboxUnreadNoticeText, _ => UpdateOtherInbox());

        _undoRefresh = Bindings.Own(new MainLoop.Coalescer(() => self.Value?.UpdateUndo()));
        Bind.Bind(nameof(MailListPageViewModel.IsUndoMailActionBarOpen), static vm => vm.IsUndoMailActionBarOpen, _ => _undoRefresh?.Request());
        Bind.Bind(nameof(MailListPageViewModel.UndoMailActionBarTitle), static vm => vm.UndoMailActionBarTitle, _ => _undoRefresh?.Request());
        Bind.Bind(nameof(MailListPageViewModel.CurrentVisibleUndoMailActionPack), static vm => vm.CurrentVisibleUndoMailActionPack, _ => _undoRefresh?.Request());
        Bind.Bind(nameof(MailListPageViewModel.UndoMailActionBarDismissInterval), static vm => vm.UndoMailActionBarDismissInterval, _ => _undoRefresh?.Request());

        Bind.Bind(nameof(MailListPageViewModel.IsBarOpen), static vm => vm.IsBarOpen, _ => UpdateUpdateBar());
        Bind.Bind(nameof(MailListPageViewModel.BarMessage), static vm => vm.BarMessage, _ => UpdateUpdateBar());
        Bind.Bind(nameof(MailListPageViewModel.BarTitle), static vm => vm.BarTitle, _ => UpdateUpdateBar());

        SelectionStateChanged += UpdateSelectAll;
        Applied += _ => UpdateSelectAll();
        UpdateSelectAll();
    }

    private void UpdatePivots()
    {
        if (IsReleased || _header is null) return;
        var pivots = ViewModel.PivotFolders.ToArray();
        WinoPivotItem[] items = [.. pivots.Select(static pivot => new WinoPivotItem(pivot.FolderTitle ?? string.Empty, pivot.ShouldDisplaySelectedItemCount ? pivot.SelectedItemCount : 0))];
        _header.Pivots.SetItems(items, Array.IndexOf(pivots, ViewModel.SelectedFolderPivot));
    }

    /// <summary>Windows FolderPivotChanged: flag the pivots, clear the selection and run the pivot command.</summary>
    private void PivotClicked(int index)
    {
        if (IsReleased) return;
        var pivots = ViewModel.PivotFolders.ToArray();
        if (index < 0 || index >= pivots.Length || ReferenceEquals(pivots[index], ViewModel.SelectedFolderPivot)) return;
        foreach (var pivot in pivots) pivot.IsSelected = false;
        pivots[index].IsSelected = true;
        ViewModel.IsMultiSelectionModeEnabled = false;
        ClearSelection();
        ViewModel.SelectedFolderPivot = pivots[index];
        Observe(ViewModel.SelectedPivotChangedCommand.ExecuteAsync(pivots[index]));
    }

    private void ShowFilterMenu(Gtk.Widget anchor)
    {
        if (IsReleased) return;
        var builder = WinoMenus.Create();
        foreach (var option in ViewModel.FilterOptions)
            builder.Radio("filter", option.Title, ReferenceEquals(option, ViewModel.SelectedFilterOption), () => RunFilter(option), icon: FilterGlyph(option.Type));
        builder.Separator();
        foreach (var option in ViewModel.SortingOptions)
            builder.Radio("sort", option.Title, ReferenceEquals(option, ViewModel.SelectedSortingOption), () => RunSorting(option), icon: SortingGlyph(option.Type));
        AppendFilterMenu(builder);
        var popover = ContextMenu.Show(anchor, builder.Build());
#if DEBUG
        DebugFilterPopover = popover;
#endif
    }

    private static WinoIconGlyph FilterGlyph(FilterOptionType type) => type switch
    {
        FilterOptionType.All => WinoIconGlyph.Mail,
        FilterOptionType.Unread => WinoIconGlyph.MarkUnread,
        FilterOptionType.Flagged => WinoIconGlyph.Flag,
        FilterOptionType.Mentions => WinoIconGlyph.NewMail,
        FilterOptionType.Files => WinoIconGlyph.Attachment,
        _ => WinoIconGlyph.None,
    };

    private static WinoIconGlyph SortingGlyph(SortingOptionType type) => type switch
    {
        SortingOptionType.Sender => WinoIconGlyph.SortTextDesc,
        SortingOptionType.ReceiveDate => WinoIconGlyph.SortLinesDesc,
        _ => WinoIconGlyph.None,
    };

    private void RunFilter(FilterOption option)
    {
        if (!IsReleased) Observe(ViewModel.SelectedFilterChangedCommand.ExecuteAsync(option));
    }

    private void RunSorting(SortingOption option)
    {
        if (!IsReleased) Observe(ViewModel.SelectedSortingChangedCommand.ExecuteAsync(option));
    }

    private void UpdateSelectAll()
    {
        if (IsReleased || _header is null) return;
        // The one mail being read is not a partial selection: outside selection mode it leaves the box unchecked
        // instead of showing the mixed state, which looked like a coloured folder glyph.
        var state = RowCount == 0 ? false : AllSelectedState;
        if (state is null && SelectedRowCount == 1 && !ViewModel.IsMultiSelectionModeEnabled) state = false;
        _header.SetSelectAll(state);
    }

    private void UpdateSyncNotice()
        => _bars?.SetSyncDisabled(ViewModel.ActiveFolder is not null && ViewModel.IsSyncButtonVisible && ViewModel.IsSynchronizationDisabledNoticeVisible);

    private void UpdateOtherInbox()
        => _bars?.SetOtherInbox(ViewModel.IsOtherInboxUnreadNoticeVisible, ViewModel.OtherInboxUnreadNoticeText ?? string.Empty);

    private void UpdateUndo()
    {
        if (IsReleased || _bars is null) return;
        if (ViewModel.IsUndoMailActionBarOpen)
            _bars.ShowUndo(ViewModel.UndoMailActionBarTitle ?? string.Empty, ViewModel.UndoMailActionBarSeverity, ViewModel.UndoMailActionBarDismissInterval);
        else _bars.HideUndo();
    }

    private void UpdateUpdateBar()
    {
        if (IsReleased || _bars is null) return;
        if (ViewModel.IsBarOpen) _bars.ShowUpdate(ViewModel.BarTitle, ViewModel.BarMessage, ViewModel.BarSeverity);
        else _bars.HideUpdate();
    }

#if DEBUG
    internal WinoMenuPopover? DebugFilterPopover { get; private set; }

    internal string DebugHeaderState()
        => $"{_header?.DescribeState()} {_bars?.DescribeState()} undo_vm={ViewModel.IsUndoMailActionBarOpen} bar_vm={ViewModel.IsBarOpen}";

    internal void DebugPivot(int index) => PivotClicked(index);

    internal void DebugClickSelectAll() => _header?.DebugClickSelectAll();

    internal void DebugUndo() => _bars?.DebugUndo();

    internal void DebugFilter(string name)
    {
        foreach (var option in ViewModel.FilterOptions)
        {
            if (string.Equals(option.Title, name, StringComparison.OrdinalIgnoreCase) || string.Equals(option.Type.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                RunFilter(option);
                return;
            }
        }
        throw new ArgumentException($"No filter '{name}'.");
    }

    internal void DebugSort(string name)
    {
        foreach (var option in ViewModel.SortingOptions)
        {
            if (string.Equals(option.Title, name, StringComparison.OrdinalIgnoreCase) || string.Equals(option.Type.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                RunSorting(option);
                return;
            }
        }
        throw new ArgumentException($"No sorting '{name}'.");
    }

    internal void DebugShowFilterMenu()
    {
        if (_header is not null) ShowFilterMenu(_header.DebugFilterButton);
    }
#endif
}
