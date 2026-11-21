using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Settings;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Settings;

/// <summary>
/// The Settings window's page list: exactly what <see cref="ISettingsCatalog"/> returns, grouped, or its search results
/// while the search entry has text. A page row navigates on selection, which is what keyboard arrows and accessibility
/// clients produce; programmatic selection never raises <see cref="PageSelected"/>.
/// </summary>
internal sealed class SettingsSidebarView : IDisposable
{
    private readonly ISettingsCatalog _catalog;
    private readonly Gtk.ListBox _list = Gtk.ListBox.New();
    private readonly Gtk.SearchEntry _search = Gtk.SearchEntry.New();
    private readonly List<(SettingsCatalogEntry Entry, Gtk.ListBoxRow Row)> _rows = [];
    private readonly List<IDisposable> _subscriptions = [];
    private WinoPage? _selected;
    private bool _applying;

    public SettingsSidebarView(ISettingsCatalog catalog)
    {
        _catalog = catalog;

        _list.AddCssClass("navigation-sidebar");
        _list.SetSelectionMode(Gtk.SelectionMode.Single);
        var list = _list;
        _subscriptions.Add(GtkSignals.Connect<Gtk.ListBox, Gtk.ListBox.RowSelectedSignalArgs>(
            h => list.OnRowSelected += h, h => list.OnRowSelected -= h, (_, args) => RowSelected(args.Row)));

        _search.SetPlaceholderText(Translator.SearchBarPlaceholder);
        WinoA11y.Label(_search, Translator.SearchBarPlaceholder);
        _search.SetHexpand(true);
        var search = _search;
        _subscriptions.Add(GtkSignals.Connect<Gtk.SearchEntry>(h => search.OnSearchChanged += h, h => search.OnSearchChanged -= h, (_, _) => Reload()));
        _subscriptions.Add(GtkSignals.Connect<Gtk.SearchEntry>(h => search.OnStopSearch += h, h => search.OnStopSearch -= h, (_, _) =>
        {
            if (!string.IsNullOrEmpty(search.GetText())) search.SetText(string.Empty);
        }));

        var searchBar = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
        searchBar.SetMarginStart(12);
        searchBar.SetMarginEnd(12);
        searchBar.SetMarginBottom(6);
        searchBar.Append(_search);

        var scroll = Gtk.ScrolledWindow.New();
        scroll.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        scroll.SetVexpand(true);
        scroll.SetChild(_list);

        var header = Adw.HeaderBar.New();
        header.SetTitleWidget(Adw.WindowTitle.New(Translator.MenuSettings, string.Empty));
        var root = Adw.ToolbarView.New();
        root.AddTopBar(header);
        root.AddTopBar(searchBar);
        root.SetContent(scroll);
        Root = root;

        Reload();
    }

    public Gtk.Widget Root { get; }

    /// <summary>A page the user selected. Not raised for <see cref="Select"/> or a rebuild.</summary>
    public event Action<WinoPage>? PageSelected;

    public SettingsCatalogEntry[] Entries => [.. _rows.Select(row => row.Entry)];

    public string SearchText
    {
        get => _search.GetText();
        set => _search.SetText(value);
    }

    /// <summary>Re-reads the catalog (language, entitlement, search text) and keeps the selected page.</summary>
    public void Reload()
    {
        _applying = true;
        try
        {
            _list.RemoveAll();
            _rows.Clear();
            var query = _search.GetText();
            var entries = string.IsNullOrWhiteSpace(query) ? _catalog.GetEntries() : _catalog.Search(query.Trim());
            foreach (var entry in entries)
            {
                var row = entry.IsHeader ? CreateHeader(entry) : CreatePageRow(entry);
                _list.Append(row);
                _rows.Add((entry, row));
            }
            ApplySelection();
        }
        finally { _applying = false; }
    }

    /// <summary>Highlights <paramref name="page"/> without raising <see cref="PageSelected"/>; null clears.</summary>
    public void Select(WinoPage? page)
    {
        _selected = page;
        _applying = true;
        try { ApplySelection(); }
        finally { _applying = false; }
    }

    public void FocusSearch() => _search.GrabFocus();

    public Gtk.Widget SearchEntry => _search;

    public void Dispose()
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        PageSelected = null;
    }

    private void ApplySelection()
    {
        foreach (var (entry, row) in _rows)
        {
            if (_selected is { } page && entry.Page == page)
            {
                _list.SelectRow(row);
                return;
            }
        }
        _list.UnselectAll();
    }

    private void RowSelected(Gtk.ListBoxRow? row)
    {
        // Before the presenter shows a page nothing is selected, and GTK selects the row keyboard focus lands on.
        if (_applying || row is null || _selected is null) return;
        foreach (var (entry, candidate) in _rows)
        {
            if (!ReferenceEquals(candidate, row) || entry.Page is not { } page) continue;
            _selected = page;
            PageSelected?.Invoke(page);
            return;
        }
    }

    private static Gtk.ListBoxRow CreateHeader(SettingsCatalogEntry entry)
    {
        var label = Gtk.Label.New(entry.Title);
        label.SetXalign(0);
        label.AddCssClass("heading");
        label.AddCssClass("dim-label");
        label.SetMarginTop(12);
        label.SetMarginStart(6);
        label.SetEllipsize(Pango.EllipsizeMode.End);
        var row = Gtk.ListBoxRow.New();
        row.SetChild(label);
        row.SetSelectable(false);
        row.SetActivatable(false);
        row.SetCanFocus(false);
        WinoA11y.Label(row, entry.Title);
        return row;
    }

    private static Gtk.ListBoxRow CreatePageRow(SettingsCatalogEntry entry)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        box.Append(new WinoIcon(entry.Icon, 16).Root);
        var label = Gtk.Label.New(entry.Title);
        label.SetXalign(0);
        label.SetHexpand(true);
        label.SetEllipsize(Pango.EllipsizeMode.End);
        box.Append(label);
        var row = Gtk.ListBoxRow.New();
        row.SetChild(box);
        if (!string.IsNullOrWhiteSpace(entry.Description)) row.SetTooltipText(entry.Description);
        WinoA11y.Label(row, entry.Title);
        return row;
    }
}
