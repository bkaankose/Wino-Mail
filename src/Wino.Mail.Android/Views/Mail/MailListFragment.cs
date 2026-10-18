using System.ComponentModel;
using Android.Content;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using AndroidX.SwipeRefreshLayout.Widget;
using CommunityToolkit.Mvvm.Messaging;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Chip;
using Google.Android.Material.ProgressIndicator;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Android.Infrastructure;
using Wino.Mail.Android.Views.Shell;
using Wino.Mail.Controls.Android.Common;
using Wino.Mail.Controls.Android.MailList;
using Wino.Mail.Controls.Core;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Data;
using Wino.Messaging.UI;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.ViewModels.Messages;
using Wino.Presentation.Android;

namespace Wino.Mail.Android.Views.Mail;

/// <summary>
/// The message list for one folder (canvas boards Inbox, InboxDark and Search; Windows MailListPage; the iPhone and
/// iPad MailListPageViewController) over the shared <see cref="MailListPageViewModel"/>. One Wino zone holds the Focused
/// and Other toggle, the filter chip and Select, the Windows info bars and the T6 list adapter over the shared
/// projection (<see cref="MailListController"/>, which outlives this view). A tap selects through the ViewModel, whose
/// active item opens the reader (or the composer for a draft). Pull to refresh and the app bar's Sync action run the
/// shell's synchronization (the Windows title-bar Sync button). Partial files: .List (adapter, selection, paging, bars),
/// .Commands (swipe, the multi-select action mode, menus, Move), .Search (search row and chips) and .Filters (the
/// filter and sort sheet or menu).
/// </summary>
public sealed partial class MailListFragment : WinoFragment<MailListPageViewModel>
{
    private const string ScrollStateKey = "mail-list-scroll";

    private MailListController? _controller;
    private AndroidSenderPictureProvider? _pictures;
    private IPreferencesService? _preferences;
    private AndroidShellController? _shell;
    private MaterialToolbar? _toolbar;
    private Extras.AndroidDailyBriefingPresenter? _briefing;
    private RecyclerView? _list;
    private WinoMailListAdapter? _adapter;
    private SwipeRefreshLayout? _refresh;
    private MaterialButtonToggleGroup? _pivots;
    private Chip? _filterChip;
    private WinoIconView? _emptyFolder;
    private WinoIconView? _filterButton;
    private global::Android.Graphics.Drawables.Drawable? _filterButtonBackground;
    private WinoInfoBar? _syncBar;
    private View? _snackbarHost;
    private Google.Android.Material.Snackbar.Snackbar? _undoSnackbar;
    private Google.Android.Material.Snackbar.Snackbar? _infoSnackbar;
    private TextView? _otherInbox;
    private View? _empty;
    private TextView? _emptyTitle;
    private WinoIconView? _emptyGlyph;
    private CircularProgressIndicator? _loading;
    private MaterialButton? _onlineSearch;
    private bool _syncBarDismissed;
    private object? _syncBarFolder;
    private bool _applyingPivots;
    private bool _refreshing;

    protected override int LayoutResource => Resource.Layout.fragment_mail_list;

    private IShellChrome? Chrome => ParentFragment as IShellChrome;

    private bool IsCompact => Chrome?.WidthClass is null or WindowWidthClass.Compact;

    /// <summary>Route hook: the projection and the reader messages live with the page (before OnNavigatedTo, as iOS binds first).</summary>
    public static void Prepare(PageEntry entry) => MailListController.Attach(entry);

    /// <summary>Route hook: the folder the page was opened for (Windows MailListPage.OnNavigatedTo), then its first load.</summary>
    public static async Task ActivatedAsync(PageEntry entry)
    {
        if (entry.ViewModel is not MailListPageViewModel viewModel || entry.Parameter is not NavigateMailFolderEventArgs folder) return;
        WeakReferenceMessenger.Default.Send(new ActiveMailFolderChangedEvent(folder.BaseFolderMenuItem, folder.FolderInitLoadAwaitTask));
        await viewModel.WaitForCurrentFolderInitializationAsync();
    }

    protected override void OnBind(View view, MailListPageViewModel viewModel, IDispatcher dispatcher)
    {
        var services = MainApplication.Services ?? throw new InvalidOperationException("The application services are not ready.");
        var context = view.RequireContext();
        _controller = Entry is { } current ? MailListController.Attach(current) : null;
        if (_controller is null) return;
        _controller.View = this;
        _pictures = services.GetRequiredService<AndroidSenderPictureProvider>();
        _preferences = viewModel.PreferencesService;
        _shell = services.GetService<AndroidNavigationService>()?.Shell;

        BuildToolbar(view, context, viewModel);
        BuildHeader(view, context, viewModel, dispatcher);
        BuildBars(view, context, viewModel, dispatcher);
        BuildList(view, context, viewModel, dispatcher);
        BuildSearch(view, context, viewModel, dispatcher);

        Bindings.Own(new ActionDisposable(() =>
        {
            EndSelectionMode();
            Chrome?.SuppressFab(false);
            if (_list?.GetLayoutManager()?.OnSaveInstanceState() is { } scroll && Entry is { } entry) entry.State[ScrollStateKey] = scroll;
            _adapter?.Detach();
            if (_controller is { } controller && ReferenceEquals(controller.View, this)) controller.View = null;
        }));
    }

    // ---- Docked search bar (canvas Mail inbox redesign: menu, "Search in <folder>", filter, overflow; no Sync button) ----

    private DockedSearchBar? _searchBar;

    private void BuildToolbar(View view, Context context, MailListPageViewModel viewModel)
    {
        _toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.mail_list_toolbar);
        if (_toolbar is null) return;
        _searchBar = DockedSearchBar.Attach(_toolbar, this, Translator.SearchBarPlaceholder, () => Chrome?.ToggleNavigation());

        _briefing = MainApplication.Services?.GetService<Extras.AndroidDailyBriefingPresenter>();
        BuildMenu(context);
        _toolbar.MenuItemClick += (_, args) =>
        {
            switch (args.Item?.ItemId)
            {
                case MenuFilter: ShowFilters(_toolbar.FindViewById(MenuFilter) ?? _toolbar); break;
                case MenuBriefing: _briefing?.Show(context, IsCompact); break;
                case MenuSelect: StartSelectionMode(null); break;
                case MenuSelectAll: ToggleSelectAll(); break;
            }
        };
        UpdateFilterChip();
        if (_shell is not null)
        {
            EventHandler<string> changed = (_, property) =>
            {
                if (property is nameof(IShellMenuProvider.SynchronizationState) or nameof(Shell.AndroidShellController.ActiveMode) or "") UpdateTitle();
            };
            _shell.Changed += changed;
            Bindings.Own(new ActionDisposable(() => _shell.Changed -= changed));
        }
        if (_briefing is { } presenter)
        {
            EventHandler briefingChanged = (_, _) => UpdateBriefingItem();
            presenter.StateChanged += briefingChanged;
            Bindings.Own(new ActionDisposable(() =>
            {
                presenter.StateChanged -= briefingChanged;
                // The sheet belongs to the mail surface it was opened over (Windows closes it on a mode switch).
                presenter.Close();
            }));
        }
        UpdateBriefingItem();
        UpdateTitle();
    }

    /// <summary>
    /// The search bar's actions: the filter (only while the folder has no Focused/Other; otherwise it sits in the header
    /// row), the daily briefing, and Select in the overflow. New mail is the FAB and Settings opens from the drawer.
    /// </summary>
    private void BuildMenu(Context context)
    {
        if (_toolbar?.Menu is not { } menu) return;
        menu.Clear();
        // The filter lives behind this icon; it fills while a filter other than All is on.
        menu.Add(0, MenuFilter, 0, Translator.Accessibility_FilterMessages)?.SetShowAsAction(ShowAsAction.Always);
        // Daily briefing (Windows title-bar button in Mail mode): only while the shared entitlement rule offers it.
        menu.Add(0, MenuBriefing, 0, Translator.DailyBriefing_Menu)?.SetShowAsAction(ShowAsAction.Always);
        menu.Add(0, MenuSelect, 1, Translator.Buttons_Multiselect);
        UpdateBriefingItem();
        UpdateFilterChip();
    }

    /// <summary>Shows the briefing action while it is available, with the unseen dot (Windows DailyBriefingUnseenBadge).</summary>
    private void UpdateBriefingItem()
    {
        if (_toolbar?.Menu?.FindItem(MenuBriefing) is not { } item || _toolbar.Context is not { } context) return;
        bool available = _briefing?.IsAvailable == true;
        item.SetVisible(available);
        if (!available) return;
        var glyph = WinoIcons.Drawable(context, WinoIconGlyph.DailyBriefing, WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph), WinoColors.SecondaryText(context));
        if (_briefing?.HasUnseenItems == true)
        {
            var dot = new global::Android.Graphics.Drawables.GradientDrawable();
            dot.SetShape(global::Android.Graphics.Drawables.ShapeType.Oval);
            dot.SetColor(WinoColors.Accent(context));
            int size = WinoTokens.Px(context, Resource.Dimension.wino_m3_briefing_new_dot);
            dot.SetSize(size, size);
            var layers = new global::Android.Graphics.Drawables.LayerDrawable([glyph, dot]);
            int offset = glyph.IntrinsicWidth - size;
            layers.SetLayerInset(1, offset, 0, 0, offset);
            item.SetIcon(layers);
            AndroidX.Core.View.MenuItemCompat.SetContentDescription(item, Translator.DailyBriefing_Menu + ", " + Translator.DailyBriefing_New);
        }
        else
        {
            item.SetIcon(glyph);
            AndroidX.Core.View.MenuItemCompat.SetContentDescription(item, Translator.DailyBriefing_Menu);
        }
    }

    private const int MenuFilter = 2;
    private const int MenuBriefing = 6;
    private const int MenuSelect = 3;

    /// <summary>"Search in <folder>" names the folder in place of a title; a finished synchronization stops pull to refresh.</summary>
    private void UpdateTitle()
    {
        if (ViewModel is not { } viewModel) return;
        if (_searchBar is not null) _searchBar.Hint = $"{Translator.SearchBar_SearchIn} {viewModel.ActiveFolder?.FolderName ?? Translator.IOS_MailboxesTitle}";
        if (_shell?.CurrentProvider?.SynchronizationState.IsSynchronizing != true) EndRefresh();
    }

    /// <summary>Pull to refresh: the shell's synchronization (the Windows title-bar Sync command), the only way to synchronize.</summary>
    private void Synchronize()
    {
        if (_shell is { } shell) _ = shell.SynchronizeAsync();
        else if (ViewModel?.SyncFolderCommand.CanExecute(null) == true) ViewModel.SyncFolderCommand.Execute(null);
    }

    // ---- Header: Focused/Other, filter chip, Select ----

    private void BuildHeader(View view, Context context, MailListPageViewModel viewModel, IDispatcher dispatcher)
    {
        _pivots = view.FindViewById<MaterialButtonToggleGroup>(Resource.Id.mail_pivots);
        _filterChip = view.FindViewById<Chip>(Resource.Id.mail_filter_chip);
        _emptyFolder = view.FindViewById<WinoIconView>(Resource.Id.mail_empty_folder);
        _filterButton = view.FindViewById<WinoIconView>(Resource.Id.mail_filter_button);

        if (_pivots is not null)
        {
            var checkedListener = new PivotListener(this);
            _pivots.AddOnButtonCheckedListener(checkedListener);
            Bindings.Own(new ActionDisposable(() => _pivots.RemoveOnButtonCheckedListener(checkedListener)));
            System.Collections.Specialized.NotifyCollectionChangedEventHandler pivotsChanged = (_, _) => _ = dispatcher.ExecuteOnUIThread(UpdatePivots);
            viewModel.PivotFolders.CollectionChanged += pivotsChanged;
            Bindings.Own(new ActionDisposable(() => viewModel.PivotFolders.CollectionChanged -= pivotsChanged));
        }
        if (_filterChip is not null)
        {
            // The active filter (canvas "filter active"): a check, its name and a clear button; only for a filter other
            // than All. Tapping it reopens the filters; its close icon returns to All.
            _filterChip.ChipIcon = WinoIcons.Drawable(context, WinoIconGlyph.Checkmark, 16, WinoColors.PrimaryText(context));
            _filterChip.ChipIconVisible = true;
            _filterChip.CloseIconVisible = true;
            _filterChip.CloseIcon = WinoIcons.Drawable(context, WinoIconGlyph.Dismiss, 12, WinoColors.SecondaryText(context));
            _filterChip.ChipBackgroundColor = global::Android.Content.Res.ColorStateList.ValueOf(WinoColors.SecondaryContainer(context));
            _filterChip.ChipStrokeWidth = 0;
            _filterChip.Click += (_, _) => ShowFilters(_filterChip);
            _filterChip.SetOnCloseIconClickListener(new ClickListener(ClearFilter));
        }
        double glyph = WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph);
        if (_emptyFolder is not null)
        {
            _emptyFolder.Icon = WinoIconGlyph.EmptyFolder;
            _emptyFolder.GlyphSize = glyph;
            _emptyFolder.ContentDescription = Translator.FolderOperation_Empty;
            _emptyFolder.Click += (_, _) => Observe(ViewModel?.EmptyFolderCommand.ExecuteAsync(null));
        }
        if (_filterButton is not null)
        {
            _filterButton.Icon = WinoIconGlyph.Filter;
            _filterButton.GlyphSize = glyph;
            _filterButton.Click += (_, _) => ShowFilters(_filterButton);
            _filterButtonBackground = _filterButton.Background;
        }

        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.ActiveFolder), vm => vm.ActiveFolder, _ => { UpdatePivots(); UpdateSyncBar(); UpdateOtherInboxLink(); UpdateTitle(); });
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.SelectedFolderPivot), vm => vm.SelectedFolderPivot, _ => { UpdatePivots(); UpdateOtherInboxLink(); });
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.IsEmptyFolderButtonVisible), vm => vm.IsEmptyFolderButtonVisible, visible =>
        {
            if (_emptyFolder is not null) _emptyFolder.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
            UpdateHeaderVisibility();
        });
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.SelectedFilterOption), vm => vm.SelectedFilterOption, _ => UpdateFilterChip());
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.SelectedSortingOption), vm => vm.SelectedSortingOption, _ => UpdateFilterChip());
    }

    private void UpdatePivots()
    {
        if (_pivots is null || ViewModel is not { } viewModel || View?.Context is not { } context) return;
        var pivots = viewModel.PivotFolders.ToArray();
        _applyingPivots = true;
        try
        {
            _pivots.RemoveAllViews();
            // A folder without Focused/Other has a single pivot: its own name, which the title already shows.
            _pivots.Visibility = pivots.Length < 2 ? ViewStates.Gone : ViewStates.Visible;
            if (pivots.Length < 2) return;
            var inflater = LayoutInflater.From(context);
            for (int index = 0; index < pivots.Length; index++)
            {
                var pivot = pivots[index];
                if (inflater?.Inflate(Resource.Layout.mail_pivot_segment, _pivots, false) is not MaterialButton button) continue;
                button.Id = View.GenerateViewId();
                button.Text = pivot.ShouldDisplaySelectedItemCount && pivot.SelectedItemCount > 0 ? $"{pivot.FolderTitle} ({pivot.SelectedItemCount})" : pivot.FolderTitle;
                button.Tag = index;
                bool selected = ReferenceEquals(pivot, viewModel.SelectedFolderPivot);
                WinoMaterialAccent.TintM3Segment(button);
                _pivots.AddView(button);
                if (selected) _pivots.Check(button.Id);
            }
        }
        finally
        {
            _applyingPivots = false;
            UpdateHeaderVisibility();
        }
    }

    /// <summary>
    /// The header row costs space only when it has something to show (canvas Mail inbox redesign): Focused/Other, Empty
    /// folder, the active filter or the search status. The filter button joins Focused/Other in that row; a folder without
    /// pivots keeps it in the search bar so no row opens just for it.
    /// </summary>
    private void UpdateHeaderVisibility()
    {
        if (View?.FindViewById(Resource.Id.mail_list_header) is not { } header) return;
        static bool Shown(View? view) => view?.Visibility == ViewStates.Visible;
        bool filterInHeader = Shown(_pivots);
        if (_filterButton is not null) _filterButton.Visibility = filterInHeader ? ViewStates.Visible : ViewStates.Gone;
        _toolbar?.Menu?.FindItem(MenuFilter)?.SetVisible(!filterInHeader);
        bool any = filterInHeader || Shown(_emptyFolder) || Shown(_filterChip) || Shown(_searchStatus);
        header.Visibility = any ? ViewStates.Visible : ViewStates.Gone;
    }

    /// <summary>Windows FolderPivotChanged: flag the pivots, clear the selection and run the pivot command.</summary>
    private void PivotClicked(int checkedId)
    {
        if (ViewModel is not { } viewModel || _pivots?.FindViewById(checkedId)?.Tag is not Java.Lang.Integer tag) return;
        int index = tag.IntValue();
        var pivots = viewModel.PivotFolders.ToArray();
        if (index < 0 || index >= pivots.Length || ReferenceEquals(pivots[index], viewModel.SelectedFolderPivot)) return;
        foreach (var pivot in pivots) pivot.IsSelected = false;
        pivots[index].IsSelected = true;
        EndSelectionMode();
        ClearSelection();
        viewModel.SelectedFolderPivot = pivots[index];
        Observe(viewModel.SelectedPivotChangedCommand.ExecuteAsync(pivots[index]));
    }

    private sealed class ClickListener(Action clicked) : Java.Lang.Object, View.IOnClickListener
    {
        public void OnClick(View? view) => clicked();
    }

    private sealed class PivotListener(MailListFragment owner) : Java.Lang.Object, MaterialButtonToggleGroup.IOnButtonCheckedListener
    {
        public void OnButtonChecked(MaterialButtonToggleGroup? group, int checkedId, bool isChecked)
        {
            if (!owner._applyingPivots && isChecked) owner.PivotClicked(checkedId);
        }
    }

    private bool IsFilterActive => ViewModel?.SelectedFilterOption is { Type: not FilterOptionType.All };

    /// <summary>The active-filter chip and the filled filter icon in the search bar and the header row.</summary>
    private void UpdateFilterChip()
    {
        if (ViewModel is not { } viewModel) return;
        bool active = IsFilterActive;
        if (_filterChip is not null)
        {
            _filterChip.Text = viewModel.SelectedFilterOption?.Title ?? Translator.FilteringOption_All;
            _filterChip.CloseIconContentDescription = $"{Translator.SearchBar_ClearAllFilters}: {_filterChip.Text}";
            _filterChip.Visibility = active ? ViewStates.Visible : ViewStates.Gone;
        }
        if (_filterButton?.Context is { } buttonContext)
        {
            // Filled while a filter other than All is on: the glyph on an accent circle, as in the search bar.
            if (active)
            {
                var circle = new global::Android.Graphics.Drawables.GradientDrawable();
                circle.SetShape(global::Android.Graphics.Drawables.ShapeType.Oval);
                circle.SetColor(WinoColors.Accent(buttonContext));
                _filterButton.Background = circle;
                _filterButton.SetTextColor(WinoColors.OnAccent(buttonContext));
            }
            else
            {
                _filterButton.Background = _filterButtonBackground;
                _filterButton.SetTextColor(WinoColors.SecondaryText(buttonContext));
            }
            _filterButton.ContentDescription = active
                ? $"{Translator.Accessibility_FilterMessages}, {viewModel.SelectedFilterOption?.Title}"
                : Translator.Accessibility_FilterMessages;
        }
        if (_toolbar?.Menu?.FindItem(MenuFilter) is { } item && _toolbar.Context is { } context)
        {
            double size = WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph);
            if (!active) item.SetIcon(WinoIcons.Drawable(context, WinoIconGlyph.Filter, size, WinoColors.SecondaryText(context)));
            else
            {
                // Filled: the glyph on an accent circle (canvas "filter active").
                var circle = new global::Android.Graphics.Drawables.GradientDrawable();
                circle.SetShape(global::Android.Graphics.Drawables.ShapeType.Oval);
                circle.SetColor(WinoColors.Accent(context));
                int diameter = WinoUnits.Dp(context, 40);
                circle.SetSize(diameter, diameter);
                var glyph = WinoIcons.Drawable(context, WinoIconGlyph.Filter, size, WinoColors.OnAccent(context));
                var layers = new global::Android.Graphics.Drawables.LayerDrawable([circle, glyph]);
                int inset = (diameter - glyph.IntrinsicWidth) / 2;
                layers.SetLayerInset(1, inset, inset, inset, inset);
                item.SetIcon(layers);
            }
            AndroidX.Core.View.MenuItemCompat.SetContentDescription(item, active
                ? $"{Translator.Accessibility_FilterMessages}, {viewModel.SelectedFilterOption?.Title}"
                : Translator.Accessibility_FilterMessages);
        }
        UpdateHeaderVisibility();
    }

    /// <summary>The active-filter chip's clear button: back to All.</summary>
    private void ClearFilter()
    {
        if (ViewModel is not { } viewModel || viewModel.FilterOptions.FirstOrDefault(static option => option.Type == FilterOptionType.All) is not { } all) return;
        Observe(viewModel.SelectedFilterChangedCommand.ExecuteAsync(all));
    }

    // ---- Shared helpers ----

    internal void Observe(Task? task)
    {
        if (task is null) return;
        _ = ObserveAsync(task);
    }

    private async Task ObserveAsync(Task task)
    {
        try { await task; }
        catch (Exception exception) { ReportError(exception); }
    }
}
