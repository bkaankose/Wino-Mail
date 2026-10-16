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
    private WinoIconView? _select;
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

    // ---- App bar (Material 3 small top app bar: menu, title, search, overflow; no Sync button) ----

    private void BuildToolbar(View view, Context context, MailListPageViewModel viewModel)
    {
        _toolbar = view.FindViewById<MaterialToolbar>(Resource.Id.mail_list_toolbar);
        if (_toolbar is null) return;
        _toolbar.SetNavigationIcon(Resource.Drawable.wino_ic_menu);
        _toolbar.NavigationContentDescription = Translator.Android_Shell_OpenNavigation;
        _toolbar.NavigationClick += (_, _) => Chrome?.ToggleNavigation();

        var menu = _toolbar.Menu;
        menu.Clear();
        var search = menu.Add(0, MenuSearch, 0, Translator.SearchBarPlaceholder);
        search?.SetIcon(WinoIcons.Drawable(context, WinoIconGlyph.Find, WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph), WinoColors.SecondaryText(context)));
        search?.SetShowAsAction(ShowAsAction.Always);
        // Daily briefing (Windows title-bar button in Mail mode): only while the shared entitlement rule offers it.
        _briefing = MainApplication.Services?.GetService<Extras.AndroidDailyBriefingPresenter>();
        var briefing = menu.Add(0, MenuBriefing, 0, Translator.DailyBriefing_Menu);
        briefing?.SetShowAsAction(ShowAsAction.Always);
        menu.Add(0, MenuSelect, 1, Translator.Buttons_Multiselect);
        menu.Add(0, MenuCompose, 2, Translator.MenuNewMail);
        menu.Add(0, MenuSettings, 3, Translator.MenuSettings);
        _toolbar.MenuItemClick += (_, args) =>
        {
            switch (args.Item?.ItemId)
            {
                case MenuSearch: OpenSearch(); break;
                case MenuBriefing: _briefing?.Show(context, IsCompact); break;
                case MenuSelect: StartSelectionMode(null); break;
                case MenuCompose: _shell?.Compose(); break;
                case MenuSettings: _shell?.OpenSettings(); break;
            }
        };
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

    private const int MenuSearch = 2;
    private const int MenuBriefing = 6;
    private const int MenuSelect = 3;
    private const int MenuCompose = 4;
    private const int MenuSettings = 5;

    /// <summary>The folder name (canvas Material pass: no subtitle line); a finished synchronization stops pull to refresh.</summary>
    private void UpdateTitle()
    {
        if (_toolbar is null || ViewModel is not { } viewModel) return;
        _toolbar.SetHeading(viewModel.ActiveFolder?.FolderName ?? Translator.IOS_MailboxesTitle);
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
        _select = view.FindViewById<WinoIconView>(Resource.Id.mail_select);

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
            _filterChip.ContentDescription = Translator.Accessibility_FilterMessages;
            _filterChip.Click += (_, _) => ShowFilters(_filterChip);
            _filterChip.SetOnCloseIconClickListener(new ClickListener(() => ShowFilters(_filterChip)));
            _filterChip.CloseIconContentDescription = Translator.Accessibility_FilterMessages;
        }
        double glyph = WinoTokens.Dp(context, Resource.Dimension.wino_m3_glyph);
        if (_emptyFolder is not null)
        {
            _emptyFolder.Icon = WinoIconGlyph.EmptyFolder;
            _emptyFolder.GlyphSize = glyph;
            _emptyFolder.ContentDescription = Translator.FolderOperation_Empty;
            _emptyFolder.Click += (_, _) => Observe(ViewModel?.EmptyFolderCommand.ExecuteAsync(null));
        }
        if (_select is not null)
        {
            _select.Icon = WinoIconGlyph.CheckmarkCircle;
            _select.GlyphSize = glyph;
            _select.ContentDescription = Translator.Buttons_Multiselect;
            _select.Click += (_, _) => StartSelectionMode(null);
        }

        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.ActiveFolder), vm => vm.ActiveFolder, _ => { UpdatePivots(); UpdateSyncBar(); UpdateOtherInboxLink(); UpdateTitle(); });
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.SelectedFolderPivot), vm => vm.SelectedFolderPivot, _ => { UpdatePivots(); UpdateOtherInboxLink(); });
        Bind(viewModel, dispatcher, nameof(MailListPageViewModel.IsEmptyFolderButtonVisible), vm => vm.IsEmptyFolderButtonVisible, visible =>
        {
            if (_emptyFolder is not null) _emptyFolder.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
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
                // Material 3 segmented buttons mark the selected segment with a check.
                button.Icon = selected ? AndroidX.Core.Content.ContextCompat.GetDrawable(context, Resource.Drawable.wino_ic_check) : null;
                WinoMaterialAccent.TintM3Segment(button);
                _pivots.AddView(button);
                if (selected) _pivots.Check(button.Id);
            }
        }
        finally { _applyingPivots = false; }
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

    private void UpdateFilterChip()
    {
        if (_filterChip is null || ViewModel is not { } viewModel) return;
        _filterChip.Text = viewModel.SelectedFilterOption?.Title ?? Translator.FilteringOption_All;
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
