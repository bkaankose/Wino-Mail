using AppKit;
using Foundation;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// The Settings window sidebar in the Windows settings pane layout: a search field, then the
/// shared settings pages with their Wino glyphs, grouped under circle-and-rule section headers
/// exactly as <see cref="Wino.Core.Domain.Models.Settings.SettingsNavigationInfoProvider"/> returns
/// them. The table is transparent so the theme backdrop shows through the pane veil; the selected
/// row draws the subtle fill and the 3pt accent pipe. While searching, ranked results replace the
/// grouped list.
/// </summary>
public sealed class SettingsSidebarViewController : NSViewController
{
    private static readonly NSColor IntelligenceBrand = WinoStyle.Hex(0x7C5CFC);

    private readonly NSTableView _table = new();
    private readonly NSSearchField _search = new();
    private readonly SidebarSource _source;
    private IReadOnlyList<SettingsSidebarEntry> _all = [];
    private WinoPage? _selectedPage;
    private bool _suppressSelection;

    public SettingsSidebarViewController()
    {
        _source = new SidebarSource(this);
    }

    /// <summary>Raised when the user picks a page.</summary>
    public event EventHandler<WinoPage>? PageSelected;

    public override void LoadView()
    {
        var root = new NSView { TranslatesAutoresizingMaskIntoConstraints = false };
        _search.PlaceholderString = Translator.SearchBarPlaceholder;
        _search.TranslatesAutoresizingMaskIntoConstraints = false;
        _search.SendsSearchStringImmediately = true;
        _search.ControlSize = NSControlSize.Regular;
        WinoAccessibility.Label(_search, Translator.SearchBarPlaceholder);
        _search.Changed += (_, _) => ApplyFilter();

        var column = new NSTableColumn("page") { ResizingMask = NSTableColumnResizing.Autoresizing };
        _table.AddColumn(column);
        _table.HeaderView = null;
        _table.Style = NSTableViewStyle.FullWidth;
        _table.RowSizeStyle = NSTableViewRowSizeStyle.Custom;
        _table.BackgroundColor = NSColor.Clear;
        _table.IntercellSpacing = new CoreGraphics.CGSize(0, 0);
        _table.SelectionHighlightStyle = NSTableViewSelectionHighlightStyle.Regular;
        _table.FloatsGroupRows = false;
        _table.AllowsEmptySelection = true;
        _table.AllowsMultipleSelection = false;
        _table.Delegate = _source;
        _table.DataSource = _source;
        WinoAccessibility.Label(_table, Translator.MenuSettings);

        var scroll = new NSScrollView
        {
            DocumentView = _table,
            HasVerticalScroller = true,
            AutohidesScrollers = true,
            DrawsBackground = false,
            BorderType = NSBorderType.NoBorder,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        scroll.AutomaticallyAdjustsContentInsets = false;
        scroll.ContentInsets = new NSEdgeInsets(2, 0, 8, 0);

        root.AddSubview(_search);
        root.AddSubview(scroll);
        NSLayoutConstraint.ActivateConstraints(
        [
            _search.TopAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TopAnchor, 4),
            _search.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor, 12),
            _search.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor, -12),
            scroll.TopAnchor.ConstraintEqualTo(_search.BottomAnchor, 6),
            scroll.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            scroll.TrailingAnchor.ConstraintEqualTo(root.TrailingAnchor),
            scroll.BottomAnchor.ConstraintEqualTo(root.BottomAnchor)
        ]);
        View = root;
        WinoStyle.AccentChanged += AccentChanged;
        Reload();
    }

    private void AccentChanged(object? sender, EventArgs args) => _table.NeedsDisplay = true;

    /// <summary>Re-reads titles, for example after the display language changes.</summary>
    public void Reload()
    {
        _all = SettingsPageCatalog.GetSidebarEntries();
        _search.PlaceholderString = Translator.SearchBarPlaceholder;
        ApplyFilter();
    }

    /// <summary>Highlights the sidebar row for a page without raising <see cref="PageSelected"/>.</summary>
    public void Select(WinoPage? page)
    {
        _selectedPage = page;
        if (!ViewLoaded) return;
        _suppressSelection = true;
        try
        {
            var index = page is null ? -1 : _source.Entries.ToList().FindIndex(entry => entry.Page == page);
            if (index >= 0)
            {
                _table.SelectRow(index, false);
                _table.ScrollRowToVisible(index);
            }
            else _table.DeselectAll(this);
        }
        finally { _suppressSelection = false; }
    }

    public void FocusSearch() => View.Window?.MakeFirstResponder(_search);

    private void ApplyFilter()
    {
        var query = _search.StringValue?.Trim();
        _source.Entries = string.IsNullOrEmpty(query) ? _all : SettingsPageCatalog.Search(query);
        _table.ReloadData();
        Select(_selectedPage);
    }

    private void RowSelected(nint row)
    {
        if (_suppressSelection || row < 0 || row >= _source.Entries.Count) return;
        if (_source.Entries[(int)row].Page is not { } page) return;
        _selectedPage = page;
        PageSelected?.Invoke(this, page);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.AccentChanged -= AccentChanged;
        base.Dispose(disposing);
    }

    private sealed class SidebarSource(SettingsSidebarViewController owner) : NSTableViewDelegate, INSTableViewDataSource
    {
        public IReadOnlyList<SettingsSidebarEntry> Entries { get; set; } = [];

        [Export("numberOfRowsInTableView:")]
        public nint GetRowCount(NSTableView tableView) => Entries.Count;

        public override bool IsGroupRow(NSTableView tableView, nint row) => false;

        public override bool ShouldSelectRow(NSTableView tableView, nint row) => row >= 0 && row < Entries.Count && !Entries[(int)row].IsHeader;

        public override nfloat GetRowHeight(NSTableView tableView, nint row)
        {
            if (row < 0 || row >= Entries.Count || !Entries[(int)row].IsHeader) return (nfloat)WinoSettingsSidebarCellView.RowHeight;
            return (nfloat)(row == 0 ? WinoSettingsSidebarCellView.FirstHeaderHeight : WinoSettingsSidebarCellView.HeaderHeight);
        }

        public override NSTableRowView CoreGetRowView(NSTableView tableView, nint row) => new WinoSettingsSidebarRowView();

        public override NSView GetViewForItem(NSTableView tableView, NSTableColumn tableColumn, nint row)
        {
            var entry = Entries[(int)row];
            if (entry.IsHeader) return WinoSettingsSidebarCellView.CreateHeader(entry.Title, entry.Icon);
            var cell = tableView.MakeView(WinoSettingsSidebarCellView.RowIdentifier, this) as WinoSettingsSidebarCellView ?? new WinoSettingsSidebarCellView();
            switch (entry.Page)
            {
                // Windows shows the app icon on the Wino account row; the Mac bundle has no icon asset yet,
                // so that row keeps its pane glyph. Wino Intelligence uses its brand colour.
                case WinoPage.WinoIntelligencePage:
                    cell.Configure(entry.Title, entry.Icon, IntelligenceBrand);
                    break;
                default:
                    cell.Configure(entry.Title, entry.Icon);
                    break;
            }
            cell.ToolTip = string.IsNullOrWhiteSpace(entry.Description) ? null : entry.Description;
            return cell;
        }

        public override void SelectionDidChange(NSNotification notification) => owner.RowSelected(owner._table.SelectedRow);
    }
}
