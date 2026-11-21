using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Messaging.Client.Shell;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Core;
using Wino.Mail.Controls.Linux.MailList;
using Wino.Mail.Linux.Infrastructure;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Messages;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Lists;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The mail list of one folder. Rows come from the shared <see cref="MailListProjection"/> (grouping and threads) and
/// <see cref="MailRowPresentation"/> (texts); they live in a list store as <see cref="MailListEntry"/> items (rows only)
/// with the date groups as GTK sections, and store order equals screen order. The selection goes back to
/// <see cref="MailListPageViewModel"/> as a snapshot, and the ViewModel decides what opens. Features extend the view
/// through partial files and the hooks below; every GTK handler reaches the view through <see cref="_self"/>.
/// </summary>
internal sealed partial class MailListView : WinoView<MailListPageViewModel>, IUsesReadingPane
{
    private const int LoadMoreDistance = 600;

    private readonly StrongBox<MailListView?> _self;
    private readonly GtkNavigationService _navigation;
    private readonly LinuxSenderPictureProvider _pictures;
    private readonly IFolderService _folders;
    private readonly IKeyboardShortcutService _shortcuts;
    private readonly MailRowCellContext _context;
    private readonly ObservableListModel<MailListEntry> _model;
    private readonly ListSections<MailListEntry> _sections;
    private readonly WinoHeaderFactory<MailListEntry> _headers;
    private readonly ConditionalWeakTable<Gtk.Widget, GroupHeaderSlot> _headerSlots = new();
    private readonly Gtk.Box _root;
    private readonly Gtk.Stack _stack;
    private readonly Gtk.ScrolledWindow _scroll;
    private readonly WinoMailGroupHeader _sticky;
    private readonly Gtk.Box _loadingMore;
    private readonly MailListStates _states = new();
    private readonly LoadMoreTrigger _loadMore;

    public MailListView(MailListPageViewModel viewModel, WinoViewServices services, LinuxSenderPictureProvider pictures, GtkNavigationService navigation,
        IFolderService folders, IKeyboardShortcutService shortcuts)
        : base(viewModel, services)
    {
        MailListPerf.ViewCreated(this);
        var self = _self = new StrongBox<MailListView?>(this);
        _navigation = navigation;
        _pictures = pictures;
        _folders = folders;
        _shortcuts = shortcuts;
        _context = new MailRowCellContext(ViewModel.PreferencesService, pictures, () => ViewModel.IsMergedAccountView, ReportError)
        {
            Bound = (cell, entry, binding) => self.Value?.OnCellBound(cell, entry, binding),
            Refreshed = (cell, model) => self.Value?.FeatureCellRefreshed(cell, model),
        };

        ViewModel.MailCollection.CoreDispatcher = Dispatcher;
        Projection = new MailListProjection(ViewModel.MailCollection.Items, ViewModel.MailListOptions ?? new MailListProjectionOptions());

        _model = new ObservableListModel<MailListEntry>(Dispatcher, ReportError);
        _sections = new ListSections<MailListEntry>(_model.Store, static (a, b) => a.GroupOrdinal.CompareTo(b.GroupOrdinal), null, ReportError);
        Selection = Gtk.MultiSelection.New(_sections.Model);
        Cells = new WinoCellFactory<MailRowCell, MailListEntry>(() => new MailRowCell(_context), ReportError)
        {
            Setup = (cell, _) => self.Value?.OnCellSetup(cell),
            Teardown = cell =>
            {
                self.Value?.OnCellTeardown(cell);
                cell.Dispose();
            },
        };
        _headers = new WinoHeaderFactory<MailListEntry>(
            () => self.Value?.CreateHeader() ?? Gtk.Box.New(Gtk.Orientation.Horizontal, 0),
            (widget, entry, _) => self.Value?.BindHeader(widget, entry),
            ReportError);

        List = Gtk.ListView.New(Selection, Cells.Factory);
        List.SetSingleClickActivate(false);
        List.SetShowSeparators(false);
        List.SetEnableRubberband(false);
        List.AddCssClass(WinoCssClass.MailList);
        WinoStyle.SetDensity(List, ViewModel.PreferencesService.MailItemDisplayMode);
        WinoA11y.Label(List, Translator.SettingsMessageList_Title);

        _scroll = Gtk.ScrolledWindow.New();
        _scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        _scroll.SetChild(List);
        _scroll.SetVexpand(true);

        _sticky = new WinoMailGroupHeader(floating: true);
        _sticky.Root.SetValign(Gtk.Align.Start);
        _sticky.Root.SetCanTarget(false);
        _sticky.Root.SetVisible(false);

        _loadingMore = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        _loadingMore.AddCssClass("osd");
        _loadingMore.AddCssClass(WinoCssClass.MailLoadingMore);
        _loadingMore.SetHalign(Gtk.Align.Center);
        _loadingMore.SetValign(Gtk.Align.End);
        _loadingMore.SetMarginBottom(12);
        _loadingMore.SetCanTarget(false);
        _loadingMore.SetVisible(false);
        var moreSpinner = Adw.Spinner.New();
        moreSpinner.SetSizeRequest(16, 16);
        _loadingMore.Append(moreSpinner);

        FloatingSlot = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        FloatingSlot.SetValign(Gtk.Align.End);
        FloatingSlot.SetMarginStart(8);
        FloatingSlot.SetMarginEnd(8);
        FloatingSlot.SetMarginBottom(8);

        var listArea = Gtk.Overlay.New();
        listArea.SetChild(_scroll);
        listArea.AddOverlay(_sticky.Root);
        listArea.AddOverlay(_loadingMore);
        listArea.AddOverlay(FloatingSlot);

        var loading = Adw.Spinner.New();
        loading.SetHalign(Gtk.Align.Center);
        loading.SetValign(Gtk.Align.Center);
        loading.SetSizeRequest(32, 32);

        _stack = Gtk.Stack.New();
        _stack.SetTransitionType(Gtk.StackTransitionType.None);
        _stack.AddNamed(loading, "loading");
        _stack.AddNamed(_states.Root, "state");
        _stack.AddNamed(listArea, "list");
        _stack.SetVisibleChildName("list");

        Toasts = Adw.ToastOverlay.New();
        Toasts.SetChild(_stack);
        Toasts.SetVexpand(true);
        HeaderSlot = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        FooterSlot = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        _root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        _root.Append(HeaderSlot);
        _root.Append(Toasts);
        _root.Append(FooterSlot);

        _loadMore = ListViews.LoadMoreNearEnd(_scroll, LoadMoreDistance, () => self.Value?.LoadMore());

        Bindings.Own(_model);
        Bindings.Own(_sections);
        Bindings.Own(_headers);
        Bindings.Own(Cells);
        Bindings.Own(_loadMore);

        var selection = Selection;
        Bindings.Own(GtkSignals.Connect<Gtk.SelectionModel, Gtk.SelectionModel.SelectionChangedSignalArgs>(
            h => selection.OnSelectionChanged += h, h => selection.OnSelectionChanged -= h,
            (_, args) => self.Value?.SelectionChanged(args.Position, args.NItems)));
        var adjustment = _scroll.GetVadjustment();
        Bindings.Own(GtkSignals.Connect<Gtk.Adjustment>(h => adjustment.OnValueChanged += h, h => adjustment.OnValueChanged -= h,
            (_, _) => self.Value?.RequestSticky()));

        EventHandler projectionChanged = (_, _) => self.Value?.ProjectionChanged();
        Projection.ProjectionChanged += projectionChanged;
        var loadMoreCommand = ViewModel.LoadMoreItemsCommand;
        System.ComponentModel.PropertyChangedEventHandler loadMoreChanged = (_, args) =>
        {
            if (args.PropertyName == nameof(IAsyncRelayCommand.IsRunning)) self.Value?.RunOnUI(() => self.Value?.UpdateLoadingMore());
        };
        loadMoreCommand.PropertyChanged += loadMoreChanged;
        var preferences = ViewModel.PreferencesService;
        EventHandler<string> preferenceChanged = (_, name) =>
        {
            if (name is nameof(IPreferencesService.MailItemDisplayMode) or nameof(IPreferencesService.IsShowPreviewEnabled)
                or nameof(IPreferencesService.IsShowSenderPicturesEnabled) or nameof(IPreferencesService.AccountNicknamePosition)
                or nameof(IPreferencesService.AlwaysShowSenderAddress) or nameof(IPreferencesService.MailTimeFormatPreference))
                self.Value?.RunOnUI(() => self.Value?.RefreshAppearance());
        };
        preferences.PreferenceChanged += preferenceChanged;
        EventHandler<string?> pictureChanged = (_, address) => self.Value?.RunOnUI(() => self.Value?.RefreshPictures(address));
        pictures.PictureChanged += pictureChanged;
        var projection = Projection;
        Bindings.Own(new ActionDisposable(() =>
        {
            projection.ProjectionChanged -= projectionChanged;
            loadMoreCommand.PropertyChanged -= loadMoreChanged;
            preferences.PreferenceChanged -= preferenceChanged;
            pictures.PictureChanged -= pictureChanged;
            CancelScheduledApply();
            CancelLoadingState();
            self.Value = null;
            projection.Dispose();
        }));

        BuildSelection();
        BuildHeader();
        SetUpFeatures();

        Bind.Bind(nameof(MailListPageViewModel.MailListOptions), static vm => vm.MailListOptions, options =>
        {
            if (IsReleased) return;
            Projection.SetOptions(options ?? new MailListProjectionOptions());
            UpdateGrouping();
        });
        Bind.BindAny(static vm => (vm.IsInitializingFolder, vm.IsEmpty, vm.IsFolderEmpty, vm.IsInSearchMode, vm.AreSearchResultsOnline, vm.HasNoOnlineSearchResult),
            _ => UpdateState());
        Subscribe<LanguageChanged>(_ => Relocalize());
    }

    public override Gtk.Widget Root => _root;

    /// <summary>Above the list: the list header and notices, the search scope bar.</summary>
    internal Gtk.Box HeaderSlot { get; }

    /// <summary>Over the bottom edge of the list: the drag banner.</summary>
    internal Gtk.Box FloatingSlot { get; }

    /// <summary>Under the list: the online search panel.</summary>
    internal Gtk.Box FooterSlot { get; }

    /// <summary>Around the list area: undo and update toasts.</summary>
    internal Adw.ToastOverlay Toasts { get; }

    internal MailListProjection Projection { get; }

    internal Gtk.ListView List { get; }

    internal Gtk.MultiSelection Selection { get; }

    internal WinoCellFactory<MailRowCell, MailListEntry> Cells { get; }

    internal Gtk.ScrolledWindow Scroller => _scroll;

    internal int RowCount => _shown.Count;

    internal MailListEntry? EntryAt(uint position) => position < _shown.Count ? _shown[(int)position] : null;

    internal uint? PositionOf(MailListEntry entry) => _model.IndexOf(entry) is >= 0 and var index ? (uint)index : null;

    internal MailListEntry? FindEntry(string rowKey)
        => _byKey.TryGetValue(rowKey, out var entry) && PositionOf(entry) is not null ? entry : null;

    internal void ScrollToEntry(MailListEntry entry)
    {
        if (PositionOf(entry) is { } position) ListViews.ScrollTo(List, position);
    }

    /// <summary>Re-maps every bound cell.</summary>
    internal void RefreshCells() => Cells.ForEachBound(static (cell, _) => cell.Refresh());

#if DEBUG
    internal static MailListView? DebugCurrent { get; private set; }
#endif

    // Hooks of the packages that extend the list. An unimplemented hook compiles to nothing.
    partial void BuildSelection();
    partial void BuildHeader();
    partial void SetupSelectionCell(MailRowCell cell);
    partial void TeardownSelectionCell(MailRowCell cell);
    partial void BindSelectionCell(MailRowCell cell, MailListEntry entry, bool isSelected);
    partial void OnApplying(ref MailListApplyContext context);
    partial void OnApplied(MailListApplyKind kind);
    partial void OnActivated(NavigationMode mode, object? parameter);
    partial void OnDeactivating();

    /// <summary>Raised after every apply, after <c>OnApplied</c>: a second consumer subscribes here instead of implementing the hook.</summary>
    private event Action<MailListApplyKind>? Applied;

    /// <summary>What a page does on every head: navigate the ViewModel, announce the folder, then wait for its first load.</summary>
    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.OnNavigatedTo(mode, parameter);
        Subscribe<ActiveMailItemChangedEvent>(OnActiveMailItemChanged);
        if (parameter is NavigateMailFolderEventArgs folder)
        {
            Messenger.Send(new ActiveMailFolderChangedEvent(folder.BaseFolderMenuItem, folder.FolderInitLoadAwaitTask));
            await ViewModel.WaitForCurrentFolderInitializationAsync();
        }
        if (IsReleased) return;
        MailListPerf.InitializationDone(this);
        _folderLoaded = true;
        UpdateState();
#if DEBUG
        DebugCurrent = this;
#endif
        OnActivated(mode, parameter);
    }

    protected override Task DeactivateAsync()
    {
#if DEBUG
        if (ReferenceEquals(DebugCurrent, this)) DebugCurrent = null;
#endif
        OnDeactivating();
        // GTK tears the cells down while the factory still listens.
        List.SetModel(null);
        ViewModel.OnNavigatedFrom(NavigationMode.Back, null);
        return Task.CompletedTask;
    }

    private void OnActiveMailItemChanged(ActiveMailItemChangedEvent message)
    {
        if (message.SelectedMailItemViewModel is not { } item) ViewModel.NavigationService.Navigate(WinoPage.IdlePage, null, NavigationReferenceFrame.RenderingFrame);
        else ViewModel.OpenItem(item);
    }

    private void OnCellSetup(MailRowCell cell)
    {
        _cellRoots[cell.Root.Handle.DangerousGetHandle()] = cell;
        SetupSelectionCell(cell);
        FeatureCellSetup(cell);
    }

    private void OnCellTeardown(MailRowCell cell)
    {
        _cellRoots.Remove(cell.Root.Handle.DangerousGetHandle());
        TeardownSelectionCell(cell);
        FeatureCellTeardown(cell);
    }

    private void OnCellBound(MailRowCell cell, MailListEntry entry, WinoCellBinding binding)
    {
        BindSelectionCell(cell, entry, binding.IsSelected);
        FeatureCellBound(cell, entry.Row);
    }

    private Gtk.Widget CreateHeader()
    {
        var header = new WinoMailGroupHeader();
        _headerSlots.Add(header.Root, new GroupHeaderSlot(header));
        return header.Root;
    }

    private void BindHeader(Gtk.Widget widget, MailListEntry entry)
    {
        if (_headerSlots.TryGetValue(widget, out var slot))
        {
            slot.Entry = entry;
            slot.Header.Title = entry.GroupTitle;
        }
        widget.SetVisible(entry.GroupTitle.Length > 0);
    }

    /// <summary>Headers keep their entry, so new group titles reach the shown headers without a re-sort.</summary>
    private void RetitleHeaders()
    {
        foreach (var (widget, slot) in _headerSlots)
        {
            if (slot.Entry is not { } entry) continue;
            slot.Header.Title = entry.GroupTitle;
            widget.SetVisible(entry.GroupTitle.Length > 0);
        }
    }

    private void LoadMore()
    {
        if (IsReleased || RowCount == 0) return;
        var command = ViewModel.LoadMoreItemsCommand;
        if (command.CanExecute(null)) command.Execute(null);
    }

    private void UpdateLoadingMore()
        => _loadingMore.SetVisible(ViewModel.LoadMoreItemsCommand.IsRunning && RowCount > 0);

    /// <summary>Row heights change with the density: the top row is put back where it was once the rows are measured again.</summary>
    private void RefreshAppearance()
    {
        var anchor = _scroll.GetVadjustment().GetValue() > 0.5 ? ListViews.CaptureTop(Cells, _scroll) : null;
        WinoStyle.SetDensity(List, ViewModel.PreferencesService.MailItemDisplayMode);
        RefreshCells();
        if (anchor is { } top && PositionOf(top.Item) is { } position) ListViews.RestoreTop(List, Cells, _scroll, top, position);
    }

    private void RefreshPictures(string? address)
    {
        var key = LinuxSenderPictureProvider.Normalize(address);
        Cells.ForEachBound((cell, _) =>
        {
            if (key is null || string.Equals(cell.Row.Model.SenderAddress.Trim(), key, StringComparison.OrdinalIgnoreCase)) cell.Refresh();
        });
    }

    private void Relocalize()
    {
        WinoA11y.Label(List, Translator.SettingsMessageList_Title);
        _states.Relocalize();
        ApplyNow(force: true);
    }

    private sealed class GroupHeaderSlot(WinoMailGroupHeader header)
    {
        public WinoMailGroupHeader Header { get; } = header;

        public MailListEntry? Entry { get; set; }
    }
}
