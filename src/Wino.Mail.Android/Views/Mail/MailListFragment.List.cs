using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using AndroidX.SwipeRefreshLayout.Widget;
using Google.Android.Material.Button;
using Google.Android.Material.ProgressIndicator;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.MailList;
using Wino.Mail.Controls.Core;
using Wino.Mail.ViewModels;
using Wino.Presentation.Android;

using Wino.Mail.Android.Infrastructure;

namespace Wino.Mail.Android.Views.Mail;

public sealed partial class MailListFragment
{
    private const int LoadMoreDistance = 10;

    // ---- List ----

    private void BuildList(View view, Context context, MailListPageViewModel viewModel, IDispatcher dispatcher)
    {
        _list = view.FindViewById<RecyclerView>(Resource.Id.mail_list);
        _refresh = view.FindViewById<SwipeRefreshLayout>(Resource.Id.mail_refresh);
        _empty = view.FindViewById(Resource.Id.mail_empty);
        _emptyTitle = view.FindViewById<TextView>(Resource.Id.mail_empty_title);
        _emptyGlyph = view.FindViewById<WinoIconView>(Resource.Id.mail_empty_glyph);
        _loading = view.FindViewById<CircularProgressIndicator>(Resource.Id.mail_loading);
        if (_emptyGlyph is not null) _emptyGlyph.GlyphSize = 40;
        if (_list is null || _controller is null) return;

        _adapter = new WinoMailListAdapter(MapRow, MailRowMapper.GroupTitle);
        _adapter.IsGrouped = (viewModel.MailListOptions?.GroupMode ?? MailListGroupMode.Date) != MailListGroupMode.None;
        var layout = new LinearLayoutManager(context);
        _list.SetLayoutManager(layout);
        _list.SetAdapter(_adapter);
        _list.ContentDescription = Translator.Accessibility_MailList;
        _adapter.Attach(_controller.Projection);
        if (Entry?.State.TryGetValue(ScrollStateKey, out var scroll) == true && scroll is IParcelable state) layout.OnRestoreInstanceState(state);

        _adapter.RowClicked += (_, row) => RowClicked(row);
        _adapter.RowLongClicked += (_, row) => StartSelectionMode(row);
        _adapter.SelectionChanged += (_, _) => SyncSelection();
        _adapter.EntriesChanged += (_, _) =>
        {
            UpdateOverlays();
            CheckLoadMore();
        };
        AttachSwipe(context);

        var paging = new PagingListener(this);
        _list.AddOnScrollListener(paging);
        Bindings.Own(new ActionDisposable(() => _list.RemoveOnScrollListener(paging)));

        if (_refresh is not null)
        {
            _refresh.SetColorSchemeColors(WinoColors.Accent(context));
            _refresh.SetProgressBackgroundColorSchemeColor(WinoColors.ZoneFill(context));
            _refresh.Refresh += (_, _) => StartRefresh();
        }

        RestoreSelection();

        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.MailListOptions), vm => vm.MailListOptions, options =>
        {
            if (_adapter is not null) _adapter.IsGrouped = (options?.GroupMode ?? MailListGroupMode.Date) != MailListGroupMode.None;
        });
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.IsInitializingFolder), vm => vm.IsInitializingFolder, _ => UpdateOverlays());
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.IsEmpty), vm => vm.IsEmpty, _ => UpdateOverlays());
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.IsInSearchMode), vm => vm.IsInSearchMode, _ => UpdateOverlays());
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.IsMultiSelectionModeEnabled), vm => vm.IsMultiSelectionModeEnabled, enabled =>
        {
            if (!enabled) EndSelectionMode();
        });
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.SelectedItemsCount), vm => vm.SelectedItemsCount, _ =>
        {
            UpdatePivots();
            UpdateActionMode();
        });
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.IsAccountSynchronizerInSynchronization), vm => vm.IsAccountSynchronizerInSynchronization, synchronizing =>
        {
            if (!synchronizing) EndRefresh();
        });

        _preferences!.PreferenceChanged += PreferenceChanged;
        _pictures!.PictureChanged += PictureChanged;
        WinoColors.AccentChanged += AppearanceChanged;
        Bindings.Own(new ActionDisposable(() =>
        {
            _preferences.PreferenceChanged -= PreferenceChanged;
            _pictures.PictureChanged -= PictureChanged;
            WinoColors.AccentChanged -= AppearanceChanged;
        }));
        UpdateOverlays();
    }

    /// <summary>Phones use Medium rows; tablets (expanded) follow the density preference, as Windows and iPad.</summary>
    private WinoMailRowModel? MapRow(MailListRow row)
    {
        if (_preferences is null || ViewModel is not { } viewModel) return null;
        var density = Chrome?.WidthClass == WindowWidthClass.Expanded
            ? Wino.Mail.ViewModels.Collections.MailRowPresentation.Density(_preferences.MailItemDisplayMode)
            : MailRowMapper.PhoneDensity;
        var address = row.SourceItem is Wino.Mail.ViewModels.Data.MailItemViewModel item ? item.MailCopy.FromAddress : null;
        return MailRowMapper.TryMap(row, _preferences, viewModel.IsMergedAccountView, density, _pictures?.Get(address));
    }

    private void PreferenceChanged(object? sender, string name)
        => Entry?.Dispatcher.ExecuteOnUIThread(() =>
        {
            if (name is nameof(IPreferencesService.IsShowPreviewEnabled) or nameof(IPreferencesService.IsShowSenderPicturesEnabled)
                or nameof(IPreferencesService.AccountNicknamePosition) or nameof(IPreferencesService.MailTimeFormatPreference)
                or nameof(IPreferencesService.MailItemDisplayMode))
                RefreshRows();
            else if (name == nameof(IPreferencesService.IsOtherInboxUnreadNoticeEnabled)) UpdateOtherInboxLink();
        });

    private void PictureChanged(object? sender, string? address) => RefreshRows();

    private void AppearanceChanged(object? sender, EventArgs args) => RefreshRows();

    /// <summary>Re-maps every bound row (preferences, pictures, accent, intelligence entitlement).</summary>
    internal void RefreshRows() => _adapter?.RefreshRows();

    private void UpdateOverlays()
    {
        if (ViewModel is not { } viewModel || _adapter is null) return;
        bool empty = !_adapter.Entries.Any(static entry => entry is WinoMailListRowEntry);
        bool loading = viewModel.IsInitializingFolder && empty;
        if (_loading is not null) _loading.Visibility = loading ? ViewStates.Visible : ViewStates.Gone;
        if (_emptyTitle is not null) _emptyTitle.Text = viewModel.IsInSearchMode ? Translator.NoMessageCrieteria : Translator.NoMessageEmptyFolder;
        if (_emptyGlyph is not null) _emptyGlyph.Icon = viewModel.IsInSearchMode ? WinoIconGlyph.Find : WinoIconGlyph.Mail;
        if (_empty is not null) _empty.Visibility = empty && !viewModel.IsInitializingFolder ? ViewStates.Visible : ViewStates.Gone;
    }

    // ---- Paging ----

    private void CheckLoadMore()
    {
        if (_list?.GetLayoutManager() is not LinearLayoutManager layout || _adapter is null || _adapter.ItemCount == 0 || ViewModel is not { } viewModel) return;
        if (layout.FindLastVisibleItemPosition() < _adapter.ItemCount - LoadMoreDistance) return;
        var command = viewModel.LoadMoreItemsCommand;
        if (command.CanExecute(null)) Observe(command.ExecuteAsync(null));
    }

    private sealed class PagingListener(MailListFragment owner) : RecyclerView.OnScrollListener
    {
        public override void OnScrolled(RecyclerView recyclerView, int dx, int dy)
        {
            if (dy > 0) owner.CheckLoadMore();
        }
    }

    // ---- Pull to refresh (the shell's Sync) ----

    private void StartRefresh()
    {
        _refreshing = true;
        Synchronize();
        // The synchronizer reports its end through the shell's synchronization state; a folder that cannot synchronize
        // never starts, so the spinner stops on its own shortly after.
        var handler = new Handler(Looper.MainLooper!);
        handler.PostDelayed(() =>
        {
            if (_shell?.CurrentProvider?.SynchronizationState.IsSynchronizing != true && ViewModel?.IsAccountSynchronizerInSynchronization != true) EndRefresh();
        }, 1500);
        handler.PostDelayed(EndRefresh, 30000);
    }

    private void EndRefresh()
    {
        if (!_refreshing) return;
        _refreshing = false;
        if (_refresh is not null) _refresh.Refreshing = false;
    }

    // ---- Selection ----

    private void RowClicked(MailListRow row)
    {
        if (_adapter is null) return;
        if (_actionMode is not null)
        {
            _adapter.SetSelected(row, !_adapter.IsSelected(row));
            return;
        }
        _adapter.SelectOnly(row);
    }

    private void SyncSelection()
    {
        if (_adapter is null || _controller is null) return;
        var selected = _adapter.SelectedRows();
        _controller.PublishSelection(selected, _actionMode is null ? selected.LastOrDefault() : null);
    }

    /// <summary>Drops the list selection and publishes the empty selection.</summary>
    internal void ClearSelection()
    {
        if (_adapter is null) return;
        if (_adapter.SelectedRows().Count == 0) _controller?.PublishSelection(Array.Empty<MailListRow>());
        else _adapter.ClearSelection();
    }

    /// <summary>Selects <paramref name="row"/> as a tap does (notifications, new drafts).</summary>
    internal void SelectRow(MailListRow row, bool scroll)
    {
        if (_adapter is null) return;
        if (_actionMode is null) _adapter.SelectOnly(row);
        else _adapter.SetSelected(row, true);
        if (scroll && _adapter.PositionOf(row) is >= 0 and var position) _list?.ScrollToPosition(position);
    }

    /// <summary>
    /// A new view of the page: the reader still open keeps its row selected; otherwise the message is no longer "open",
    /// so a tap opens it again (iOS clears the selection when the list comes back).
    /// </summary>
    private void RestoreSelection()
    {
        if (_adapter is null || ViewModel is not { } viewModel || _controller is null) return;
        var navigation = MainApplication.Services?.GetService(typeof(Infrastructure.AndroidNavigationService)) as Infrastructure.AndroidNavigationService;
        if (navigation?.RenderingEntry is null)
        {
            _controller.PublishSelection(Array.Empty<MailListRow>());
            return;
        }
        var ids = viewModel.SelectedItems.Select(static item => item.UniqueId).ToHashSet();
        foreach (var entry in _adapter.Entries.OfType<WinoMailListRowEntry>())
            if (entry.Row.LeafItems.Any(leaf => ids.Contains(leaf.StableId))) _adapter.SetSelected(entry.Row, true);
    }

    // ---- Bars (Windows SyncDisabledInfoBar, OtherInboxUnreadNoticeLink) and snackbars (undo, status) ----

    private void BuildBars(View view, Context context, MailListPageViewModel viewModel, IDispatcher dispatcher)
    {
        _syncBar = view.FindViewById<WinoInfoBar>(Resource.Id.mail_sync_bar);
        _snackbarHost = view.FindViewById(Resource.Id.mail_snackbar_host);
        _otherInbox = view.FindViewById<TextView>(Resource.Id.mail_other_inbox);
        _onlineSearch = view.FindViewById<MaterialButton>(Resource.Id.mail_online_search);

        if (_syncBar is not null)
        {
            _syncBar.Severity = WinoInfoBarSeverity.Informational;
            _syncBar.Title = Translator.InfoBarTitle_SynchronizationDisabledFolder;
            _syncBar.Message = Translator.InfoBarMessage_SynchronizationDisabledFolder;
            _syncBar.ActionTitle = Translator.MacOS_MailList_EnableFolderSync;
            _syncBar.IsClosable = true;
            _syncBar.ActionInvoked += (_, _) => Observe(ViewModel?.EnableFolderSynchronizationCommand.ExecuteAsync(null));
            _syncBar.Closed += (_, _) => { _syncBarDismissed = true; UpdateSyncBar(); };
        }
        if (_otherInbox is not null) _otherInbox.Click += (_, _) => Observe(ViewModel?.OpenOtherInboxCommand.ExecuteAsync(null));
        Bindings.Own(new ActionDisposable(() =>
        {
            _undoSnackbar?.Dismiss();
            _infoSnackbar?.Dismiss();
            _undoSnackbar = _infoSnackbar = null;
        }));
        if (_onlineSearch is not null)
        {
            _onlineSearch.Text = $"{Translator.OnlineSearchTry_Line1} {Translator.OnlineSearchTry_Line2}";
            _onlineSearch.Icon = WinoIcons.Drawable(context, WinoIconGlyph.GlobeSearch, WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph_small), WinoColors.AccentText(context));
            _onlineSearch.Click += (_, _) => Observe(RunOnlineSearchAsync());
        }

        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.IsOnlineSearchButtonVisible), vm => vm.IsOnlineSearchButtonVisible, visible =>
        {
            if (_onlineSearch is not null) _onlineSearch.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
        });
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.IsBarOpen), vm => vm.IsBarOpen, _ => UpdateInfoBar());
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.BarMessage), vm => vm.BarMessage, _ => UpdateInfoBar());
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.BarTitle), vm => vm.BarTitle, _ => UpdateInfoBar());
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.IsFolderSynchronizationEnabled), vm => vm.IsFolderSynchronizationEnabled, _ => UpdateSyncBar());
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.IsSyncButtonVisible), vm => vm.IsSyncButtonVisible, _ => UpdateSyncBar());
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.IsOtherInboxUnreadNoticeVisible), vm => vm.IsOtherInboxUnreadNoticeVisible, _ => UpdateOtherInboxLink());
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.OtherInboxUnreadNoticeText), vm => vm.OtherInboxUnreadNoticeText, _ => UpdateOtherInboxLink());
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.IsUndoMailActionBarOpen), vm => vm.IsUndoMailActionBarOpen, _ => UpdateUndoBar());
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.UndoMailActionBarTitle), vm => vm.UndoMailActionBarTitle, _ => UpdateUndoBar());
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.CurrentVisibleUndoMailActionPack), vm => vm.CurrentVisibleUndoMailActionPack, _ => RestartUndoCountdown());
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.UndoMailActionBarDismissInterval), vm => vm.UndoMailActionBarDismissInterval, _ => RestartUndoCountdown());
    }

    private void UpdateSyncBar()
    {
        if (_syncBar is null || ViewModel is not { } viewModel) return;
        var folder = viewModel.ActiveFolder;
        if (!ReferenceEquals(folder, _syncBarFolder))
        {
            _syncBarFolder = folder;
            _syncBarDismissed = false;
        }
        bool show = folder is not null && viewModel.IsSyncButtonVisible && !viewModel.IsFolderSynchronizationEnabled && !_syncBarDismissed;
        _syncBar.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
    }

    private void UpdateOtherInboxLink()
    {
        if (_otherInbox is null || ViewModel is not { } viewModel) return;
        bool show = viewModel.IsOtherInboxUnreadNoticeVisible;
        _otherInbox.Visibility = show ? ViewStates.Visible : ViewStates.Gone;
        if (show) _otherInbox.Text = viewModel.OtherInboxUnreadNoticeText ?? string.Empty;
    }

    /// <summary>The shared status bar (BarTitle, BarMessage) as a short Material 3 snackbar.</summary>
    private void UpdateInfoBar()
    {
        if (ViewModel is not { } viewModel) return;
        bool open = viewModel.IsBarOpen && !string.IsNullOrWhiteSpace(viewModel.BarMessage);
        if (!open)
        {
            _infoSnackbar?.Dismiss();
            return;
        }
        var text = string.IsNullOrWhiteSpace(viewModel.BarTitle) ? viewModel.BarMessage! : $"{viewModel.BarTitle} {viewModel.BarMessage}";
        _infoSnackbar = ShowSnackbar(text, (int)StatusSnackbarDuration.TotalMilliseconds, null, null, () =>
        {
            if (ViewModel is { } vm) vm.IsBarOpen = false;
        });
    }

    private static readonly TimeSpan StatusSnackbarDuration = TimeSpan.FromSeconds(2);

    /// <summary>The undo bar (Windows UndoMailActionBar) as a Material 3 snackbar with Undo, for the shared interval.</summary>
    private void UpdateUndoBar()
    {
        if (ViewModel is not { } viewModel) return;
        if (!viewModel.IsUndoMailActionBarOpen)
        {
            _undoSnackbar?.Dismiss();
            return;
        }
        if (_undoSnackbar is { IsShownOrQueued: true } && _undoTitle == viewModel.UndoMailActionBarTitle) return;
        ShowUndoSnackbar();
    }

    /// <summary>A new pack or interval while the bar is up gets a full countdown again.</summary>
    private void RestartUndoCountdown()
    {
        if (ViewModel?.IsUndoMailActionBarOpen == true) ShowUndoSnackbar();
        else _undoSnackbar?.Dismiss();
    }

    private string? _undoTitle;

    private void ShowUndoSnackbar()
    {
        if (ViewModel is not { } viewModel) return;
        _undoTitle = viewModel.UndoMailActionBarTitle;
        _undoSnackbar = ShowSnackbar(_undoTitle ?? string.Empty, (int)TimeSpan.FromSeconds(viewModel.UndoMailActionBarDismissInterval).TotalMilliseconds,
            Translator.UndoActions_Button, () => Observe(ViewModel?.UndoLatestQueuedActionCommand.ExecuteAsync(null)),
            () => { if (ViewModel is { } vm) vm.IsUndoMailActionBarOpen = false; });
    }

    /// <summary>
    /// Shows a snackbar in the list pane above the compact FAB. <paramref name="dismissed"/> runs when it times out or is
    /// swiped away, not when another snackbar replaces it.
    /// </summary>
    private Google.Android.Material.Snackbar.Snackbar? ShowSnackbar(string text, int durationMs, string? action, Action? invoked, Action dismissed)
    {
        if (_snackbarHost is null) return null;
        var snackbar = Google.Android.Material.Snackbar.Snackbar.Make(_snackbarHost, text, durationMs);
        if (action is not null && invoked is not null) snackbar.SetAction(action, _ => invoked());
        if (Chrome?.SnackbarAnchor is { } anchor) snackbar.SetAnchorView(anchor);
        snackbar.AddCallback(new SnackbarDismissed(dismissed));
        snackbar.Show();
        return snackbar;
    }

    private sealed class SnackbarDismissed(Action dismissed) : Google.Android.Material.Snackbar.Snackbar.Callback
    {
        public override void OnDismissed(Google.Android.Material.Snackbar.Snackbar? transientBottomBar, int e)
        {
            if (e is DismissEventConsecutive or DismissEventManual) return;
            dismissed();
        }
    }
}
