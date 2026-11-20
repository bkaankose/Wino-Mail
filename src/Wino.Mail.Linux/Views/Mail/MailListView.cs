using System.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Navigation;
using Wino.Mail.Controls.Core;
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
internal sealed class MailListView : IRecipient<ActiveMailItemChangedEvent>
{
    private sealed record Entry(string Key, string? Header, MailListRow? Row);

    private readonly IDispatcher _dispatcher;
    private readonly IMessenger _messenger;
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
    private bool _released;

    public MailListView(IServiceProvider services, IDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _messenger = services.GetRequiredService<IMessenger>();
        ViewModel = services.GetRequiredService<MailListPageViewModel>();
        ViewModel.Dispatcher = dispatcher;
        ViewModel.MailCollection.CoreDispatcher = dispatcher;
        _projection = new MailListProjection(ViewModel.MailCollection.Items, ViewModel.MailListOptions ?? new MailListProjectionOptions());
        _projection.ProjectionChanged += (_, _) => QueueRebuild();
        ViewModel.PropertyChanged += ViewModelChanged;

        _selection = Gtk.SingleSelection.New(_keys);
        _selection.SetAutoselect(false);
        _selection.SetCanUnselect(true);
        _selection.OnSelectionChanged += (_, _) => SelectionChanged();

        // The model holds row keys only; a cell looks its row up by position, so no GObject subclass is needed.
        var factory = Gtk.SignalListItemFactory.New();
        factory.OnSetup += (_, args) =>
        {
            var item = (Gtk.ListItem)args.Object;
            var cell = new MailRowCell();
            _cells[cell.Root.Handle.DangerousGetHandle()] = cell;
            item.SetChild(cell.Root);
        };
        factory.OnBind += (_, args) =>
        {
            var item = (Gtk.ListItem)args.Object;
            if (CellOf(item) is not { } cell || item.GetPosition() >= _entries.Count) return;
            var entry = _entries[(int)item.GetPosition()];
            item.SetSelectable(entry.Row is not null);
            item.SetActivatable(entry.Row is not null);
            cell.Bind(entry.Header, entry.Row, ViewModel.PreferencesService.MailTimeFormatPreference);
        };
        factory.OnUnbind += (_, args) => CellOf((Gtk.ListItem)args.Object)?.Unbind();

        var list = Gtk.ListView.New(_selection, factory);
        list.SetSingleClickActivate(false);
        _scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        _scroll.SetChild(list);
        _scroll.GetVadjustment().OnValueChanged += (_, _) => LoadMoreIfNeeded();

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
        UpdateState();
    }

    public Gtk.Widget Root { get; }
    public MailListPageViewModel ViewModel { get; }

    /// <summary>What a page does on every head: navigate the ViewModel, announce the folder, then wait for its first load.</summary>
    public async Task ActivateAsync(object? parameter)
    {
        ViewModel.OnNavigatedTo(NavigationMode.New, parameter);
        _messenger.Register(this);
        if (parameter is NavigateMailFolderEventArgs folder)
        {
            _title.SetTitle(folder.BaseFolderMenuItem?.FolderName ?? string.Empty);
            _messenger.Send(new ActiveMailFolderChangedEvent(folder.BaseFolderMenuItem, folder.FolderInitLoadAwaitTask));
            await ViewModel.WaitForCurrentFolderInitializationAsync();
        }
        if (!_released) ViewModel.OnPageLoaded();
    }

    public void Release()
    {
        if (_released) return;
        _released = true;
        _messenger.UnregisterAll(this);
        ViewModel.PropertyChanged -= ViewModelChanged;
        foreach (var cell in _cells.Values) cell.Unbind();
        _cells.Clear();
        _projection.Dispose();
        ViewModel.OnNavigatedFrom(NavigationMode.Back, null);
    }

    public void Receive(ActiveMailItemChangedEvent message)
    {
        var item = message.SelectedMailItemViewModel;
        _ = _dispatcher.ExecuteOnUIThread(() =>
        {
            if (_released) return;
            if (item is null) ViewModel.NavigationService.Navigate(WinoPage.IdlePage, null, NavigationReferenceFrame.RenderingFrame);
            else ViewModel.OpenItem(item);
        });
    }

    private MailRowCell? CellOf(Gtk.ListItem item)
        => item.GetChild() is { } child && _cells.TryGetValue(child.Handle.DangerousGetHandle(), out var cell) ? cell : null;

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MailListPageViewModel.MailListOptions))
            _ = _dispatcher.ExecuteOnUIThread(() => { if (!_released) _projection.SetOptions(ViewModel.MailListOptions ?? new MailListProjectionOptions()); });
        else if (args.PropertyName is nameof(MailListPageViewModel.IsInitializingFolder) or nameof(MailListPageViewModel.IsEmpty) or nameof(MailListPageViewModel.IsFolderEmpty))
            _ = _dispatcher.ExecuteOnUIThread(UpdateState);
    }

    private void UpdateState()
    {
        if (_released) return;
        _stack.SetVisibleChildName(_entries.Count > 0 ? "list" : ViewModel.IsInitializingFolder ? "loading" : "empty");
    }

    private void QueueRebuild()
    {
        if (_rebuildQueued || _released) return;
        _rebuildQueued = true;
        GLib.Functions.IdleAdd(GLib.Constants.PRIORITY_DEFAULT_IDLE, () =>
        {
            _rebuildQueued = false;
            if (!_released) Rebuild();
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
        if (_applying || _released) return;
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
        if (_released) return;
        var adjustment = _scroll.GetVadjustment();
        if (adjustment.GetValue() + adjustment.GetPageSize() < adjustment.GetUpper() - 600) return;
        if (ViewModel.LoadMoreItemsCommand.CanExecute(null)) ViewModel.LoadMoreItemsCommand.Execute(null);
    }
}
