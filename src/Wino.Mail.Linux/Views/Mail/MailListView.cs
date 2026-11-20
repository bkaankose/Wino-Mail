using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Core;
using Wino.Mail.Linux.Infrastructure;
using Wino.Presentation.Linux;
using Wino.Mail.ViewModels;
using Wino.Mail.ViewModels.Collections;
using Wino.Mail.ViewModels.Data;
using Wino.Mail.ViewModels.Messages;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The mail list of one folder. Rows come from the shared <see cref="MailListProjection"/> (grouping and threads) and
/// <see cref="MailRowPresentation"/> (texts); the selection goes back to <see cref="MailListPageViewModel"/> as a
/// snapshot, and the ViewModel decides what opens.
/// </summary>
internal sealed class MailListView : WinoView<MailListPageViewModel>
{
    private sealed record Entry(string Key, string? Header, MailListRow? Row);

    private readonly MailListProjection _projection;
    private readonly Gtk.StringList _keys = Gtk.StringList.New([]);
    private readonly Gtk.SingleSelection _selection;
    private readonly Gtk.ScrolledWindow _scroll = Gtk.ScrolledWindow.New();
    private readonly Gtk.Stack _stack = Gtk.Stack.New();
    private readonly Adw.WindowTitle _title = Adw.WindowTitle.New(string.Empty, string.Empty);
    private readonly Dictionary<nint, MailRowCell> _cells = [];
    private List<Entry> _entries = [];
    private string _publishedSelection = string.Empty;
    private bool _applying;
    private bool _rebuildQueued;

    public MailListView(MailListPageViewModel viewModel, WinoViewServices services) : base(viewModel, services)
    {
        ViewModel.MailCollection.CoreDispatcher = Dispatcher;
        _projection = new MailListProjection(ViewModel.MailCollection.Items, ViewModel.MailListOptions ?? new MailListProjectionOptions());
        _projection.ProjectionChanged += (_, _) => QueueRebuild();

        _selection = Gtk.SingleSelection.New(_keys);
        _selection.SetAutoselect(false);
        _selection.SetCanUnselect(true);
        // Gir.Core 0.8.1 keeps a disconnected handler in the object's closure table, so a handler that captured the view
        // would root it while GTK holds the object. The handlers reach the view through this box, emptied on release.
        var self = new StrongBox<MailListView?>(this);
        GObject.SignalHandler<Gtk.SelectionModel, Gtk.SelectionModel.SelectionChangedSignalArgs> selectionChanged = (_, _) => self.Value?.SelectionChanged();
        var selection = _selection;
        selection.OnSelectionChanged += selectionChanged;

        // The model holds row keys only; a cell looks its row up by position, so no GObject subclass is needed.
        var factory = Gtk.SignalListItemFactory.New();
        GObject.SignalHandler<Gtk.SignalListItemFactory, Gtk.SignalListItemFactory.SetupSignalArgs> setup = (_, args) => self.Value?.SetupCell((Gtk.ListItem)args.Object);
        GObject.SignalHandler<Gtk.SignalListItemFactory, Gtk.SignalListItemFactory.BindSignalArgs> bind = (_, args) => self.Value?.BindCell((Gtk.ListItem)args.Object);
        GObject.SignalHandler<Gtk.SignalListItemFactory, Gtk.SignalListItemFactory.UnbindSignalArgs> unbind = (_, args) => self.Value?.CellOf((Gtk.ListItem)args.Object)?.Unbind();
        GObject.SignalHandler<Gtk.SignalListItemFactory, Gtk.SignalListItemFactory.TeardownSignalArgs> teardown = (_, args) => self.Value?.TeardownCell((Gtk.ListItem)args.Object);
        factory.OnSetup += setup;
        factory.OnBind += bind;
        factory.OnUnbind += unbind;
        factory.OnTeardown += teardown;

        var list = Gtk.ListView.New(selection, factory);
        list.SetSingleClickActivate(false);
        _scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        _scroll.SetChild(list);
        var adjustment = _scroll.GetVadjustment();
        GObject.SignalHandler<Gtk.Adjustment> scrolled = (_, _) => self.Value?.LoadMoreIfNeeded();
        adjustment.OnValueChanged += scrolled;

        Bindings.Own(new ActionDisposable(() =>
        {
            self.Value = null;
            selection.OnSelectionChanged -= selectionChanged;
            factory.OnSetup -= setup;
            factory.OnBind -= bind;
            factory.OnUnbind -= unbind;
            factory.OnTeardown -= teardown;
            adjustment.OnValueChanged -= scrolled;
        }));

        var loading = Adw.Spinner.New();
        loading.SetHalign(Gtk.Align.Center);
        loading.SetValign(Gtk.Align.Center);
        loading.SetSizeRequest(32, 32);
        var empty = Adw.StatusPage.New();
        empty.SetIconName("mail-read-symbolic");
        empty.SetTitle(Translator.NoMessageEmptyFolder);
        empty.AddCssClass("compact");
        _stack.AddNamed(loading, "loading");
        _stack.AddNamed(empty, "empty");
        _stack.AddNamed(_scroll, "list");

        var header = Adw.HeaderBar.New();
        header.SetTitleWidget(_title);
        header.SetShowStartTitleButtons(false);
        header.SetShowEndTitleButtons(false);
        var view = Adw.ToolbarView.New();
        view.AddTopBar(header);
        view.SetContent(_stack);
        Root = view;

        Bind.Bind(nameof(MailListPageViewModel.MailListOptions), static vm => vm.MailListOptions, options =>
        {
            if (!IsReleased) _projection.SetOptions(options ?? new MailListProjectionOptions());
        });
        Bind.BindAny(static vm => (vm.IsInitializingFolder, vm.IsEmpty, vm.IsFolderEmpty), _ => UpdateState());
    }

    public override Gtk.Widget Root { get; }

    /// <summary>What a page does on every head: navigate the ViewModel, announce the folder, then wait for its first load.</summary>
    protected override async Task InitializeAsync(NavigationMode mode, object? parameter)
    {
        ViewModel.OnNavigatedTo(mode, parameter);
        Subscribe<ActiveMailItemChangedEvent>(OnActiveMailItemChanged);
        if (parameter is NavigateMailFolderEventArgs folder)
        {
            _title.SetTitle(folder.BaseFolderMenuItem?.FolderName ?? string.Empty);
            Messenger.Send(new ActiveMailFolderChangedEvent(folder.BaseFolderMenuItem, folder.FolderInitLoadAwaitTask));
            await ViewModel.WaitForCurrentFolderInitializationAsync();
        }
    }

    protected override Task DeactivateAsync()
    {
        foreach (var cell in _cells.Values) cell.Unbind();
        _cells.Clear();
        _projection.Dispose();
        ViewModel.OnNavigatedFrom(NavigationMode.Back, null);
        return Task.CompletedTask;
    }

    private void OnActiveMailItemChanged(ActiveMailItemChangedEvent message)
    {
        if (message.SelectedMailItemViewModel is not { } item) ViewModel.NavigationService.Navigate(WinoPage.IdlePage, null, NavigationReferenceFrame.RenderingFrame);
        else ViewModel.OpenItem(item);
    }

    private void SetupCell(Gtk.ListItem item)
    {
        var cell = new MailRowCell();
        _cells[cell.Root.Handle.DangerousGetHandle()] = cell;
        item.SetChild(cell.Root);
    }

    private void BindCell(Gtk.ListItem item)
    {
        if (CellOf(item) is not { } cell || item.GetPosition() >= _entries.Count) return;
        var entry = _entries[(int)item.GetPosition()];
        item.SetSelectable(entry.Row is not null);
        item.SetActivatable(entry.Row is not null);
        cell.Bind(entry.Header, entry.Row, ViewModel.PreferencesService.MailTimeFormatPreference);
    }

    private void TeardownCell(Gtk.ListItem item)
    {
        if (item.GetChild() is { } child) _cells.Remove(child.Handle.DangerousGetHandle());
    }

    private MailRowCell? CellOf(Gtk.ListItem item)
        => item.GetChild() is { } child && _cells.TryGetValue(child.Handle.DangerousGetHandle(), out var cell) ? cell : null;

    private void UpdateState()
    {
        if (IsReleased) return;
        _stack.SetVisibleChildName(_entries.Count > 0 ? "list" : ViewModel.IsInitializingFolder ? "loading" : "empty");
    }

    private void QueueRebuild()
    {
        if (_rebuildQueued || IsReleased) return;
        _rebuildQueued = true;
        GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT_IDLE, () =>
        {
            _rebuildQueued = false;
            if (!IsReleased) Rebuild();
            return false;
        });
    }

    private void Rebuild()
    {
        var entries = new List<Entry>();
        foreach (var group in _projection.Groups)
        {
            var title = MailRowPresentation.GroupTitle(group.Key);
            if (!string.IsNullOrEmpty(title)) entries.Add(new Entry("group:" + title, title, null));
            foreach (var row in group) entries.Add(new Entry(MailListSelectionSnapshotBuilder.RowKey(row), null, row));
        }

        // A page loaded at the end only appends, which keeps the scroll position; anything else replaces the model.
        var common = 0;
        while (common < _entries.Count && common < entries.Count && _entries[common].Key == entries[common].Key) common++;
        var selectedKey = _selection.GetSelected() is var position && position < _entries.Count ? _entries[(int)position].Key : null;
        var removed = (uint)(_entries.Count - common);

        _applying = true;
        _entries = entries;
        _keys.Splice((uint)common, removed, [.. entries.Skip(common).Select(static entry => entry.Key)]);
        if (selectedKey is not null && entries.FindIndex(entry => entry.Key == selectedKey) is >= 0 and var index) _selection.SetSelected((uint)index);
        _applying = false;

        UpdateState();
        LoadMoreIfNeeded();
    }

    private void SelectionChanged()
    {
        if (_applying || IsReleased) return;
        var position = _selection.GetSelected();
        var row = position < _entries.Count ? _entries[(int)position].Row : null;
        MailListRow[] selected = row is null ? [] : [row];
        var result = MailListSelectionSnapshotBuilder.Build(selected, _projection, row);
        if (result.Signature == _publishedSelection) return;
        _publishedSelection = result.Signature;
        ViewModel.ApplyMailSelectionSnapshot(result.Snapshot);
    }

    private void LoadMoreIfNeeded()
    {
        if (IsReleased) return;
        var adjustment = _scroll.GetVadjustment();
        if (adjustment.GetValue() + adjustment.GetPageSize() < adjustment.GetUpper() - 600) return;
        if (ViewModel.LoadMoreItemsCommand.CanExecute(null)) ViewModel.LoadMoreItemsCommand.Execute(null);
    }
}
