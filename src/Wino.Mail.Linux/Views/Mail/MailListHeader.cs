using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Linux.MailList;
using Wino.Presentation;
using Wino.Presentation.Linux;
using Wino.Presentation.Linux.Style;

namespace Wino.Mail.Linux.Views.Mail;

/// <summary>
/// The header row of the mail list (Windows "Pivot + Sync + Multi Select", macOS <c>BuildListPane</c>): select all,
/// folder pivots, online-results glyph, Empty folder, select mode and the filter and sort button. It knows no ViewModel.
/// </summary>
internal sealed class MailListHeader : IDisposable
{
    private readonly Gtk.Box _root;
    private readonly Gtk.CheckButton _selectAll;
    private readonly Gtk.Widget _online;
    private readonly Gtk.Button _empty;
    private readonly Gtk.ToggleButton _mode;
    private readonly Gtk.Button _filter;
    private readonly Gtk.Label _filterTitle;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _applying;
    private bool _disposed;

    public MailListHeader()
    {
        WinoLiveObjects.Track(this);
        _root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        ActionBarSlot = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        _root.Append(ActionBarSlot);

        var row = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
        row.AddCssClass(WinoCssClass.MailListHeader);
        row.AddCssClass(WinoZone.SurfaceClass(WinoThemeSurface.MailListHeader));
        row.SetMarginTop(6);
        row.SetMarginBottom(6);
        row.SetMarginStart(10);
        row.SetMarginEnd(6);

        _selectAll = Gtk.CheckButton.New();
        _selectAll.SetValign(Gtk.Align.Center);
        WinoA11y.LabelWithTooltip(_selectAll, Translator.Accessibility_SelectAllMessages);
        row.Append(_selectAll);

        Pivots = new WinoPivotBar();
        row.Append(Pivots.Root);

        _online = new WinoIcon(WinoIconGlyph.GlobeSearch, 16, WinoIconMode.Mono).Root;
        _online.AddCssClass("dim-label");
        _online.SetTooltipText(Translator.SettingsAppPreferences_SearchMode_Online);
        _online.SetValign(Gtk.Align.Center);
        _online.SetVisible(false);
        row.Append(_online);

        _empty = WinoIcons.Button(WinoIconGlyph.EmptyFolder, Translator.FolderOperation_Empty, 18);
        _empty.SetVisible(false);
        row.Append(_empty);

        _mode = WinoIcons.ToggleButton(WinoIconGlyph.MultiSelect, Translator.Buttons_Multiselect);
        row.Append(_mode);

        _filter = Gtk.Button.New();
        _filter.AddCssClass("flat");
        _filter.SetValign(Gtk.Align.Center);
        var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
        content.Append(WinoIcons.Label(WinoIconGlyph.Filter, 14));
        _filterTitle = Gtk.Label.New(string.Empty);
        _filterTitle.SetEllipsize(Pango.EllipsizeMode.End);
        _filterTitle.SetMaxWidthChars(10);
        content.Append(_filterTitle);
        content.Append(Gtk.Image.NewFromIconName("pan-down-symbolic"));
        _filter.SetChild(content);
        _filter.SetTooltipText(Translator.Accessibility_FilterMessages);
        SetFilterTitle(string.Empty);
        row.Append(_filter);

        _root.Append(row);

        _subscriptions.Add(GtkSignals.OnToggled(_selectAll, SelectAllChanged));
        _subscriptions.Add(GtkSignals.OnClicked(_empty, () => EmptyFolderRequested?.Invoke()));
        _subscriptions.Add(GtkSignals.OnToggled(_mode, () =>
        {
            if (!_applying) SelectionModeToggled?.Invoke(_mode.GetActive());
        }));
        _subscriptions.Add(GtkSignals.OnClicked(_filter, () => FilterMenuRequested?.Invoke(_filter)));
    }

    public Gtk.Widget Root => _root;

    /// <summary>The optional mail list action bar (Windows ActionBarGrid); nothing is built in it yet.</summary>
    public Gtk.Box ActionBarSlot { get; }

    public WinoPivotBar Pivots { get; }

    /// <summary>True: select all; false: clear.</summary>
    public event Action<bool>? SelectAllToggled;

    public event Action? EmptyFolderRequested;

    public event Action<bool>? SelectionModeToggled;

    /// <summary>The filter button, as the popover anchor.</summary>
    public event Action<Gtk.Widget>? FilterMenuRequested;

    /// <summary>Null is the mixed state.</summary>
    public void SetSelectAll(bool? state)
    {
        _applying = true;
        try
        {
            _selectAll.SetInconsistent(state is null);
            _selectAll.SetActive(state == true);
        }
        finally { _applying = false; }
    }

    public void SetOnlineResults(bool visible) => _online.SetVisible(visible);

    public void SetEmptyFolderVisible(bool visible) => _empty.SetVisible(visible);

    public void SetSelectionMode(bool active)
    {
        if (_mode.GetActive() == active) return;
        _applying = true;
        try { _mode.SetActive(active); }
        finally { _applying = false; }
    }

    public void SetFilterTitle(string title)
    {
        _filterTitle.SetText(title);
        WinoA11y.Label(_filter, string.IsNullOrEmpty(title) ? Translator.Accessibility_FilterMessages : $"{Translator.Accessibility_FilterMessages}: {title}");
    }

#if DEBUG
    internal Gtk.Widget DebugFilterButton => _filter;

    internal void DebugClickSelectAll() => _selectAll.Activate();

    internal string DescribeState()
        => $"pivots=[{Pivots.Describe()}] filter={_filterTitle.GetText()} select_all={(_selectAll.GetInconsistent() ? "mixed" : _selectAll.GetActive().ToString())} "
            + $"mode={_mode.GetActive()} online={_online.GetVisible()} empty={_empty.GetVisible()}";
#endif

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        Pivots.Dispose();
        SelectAllToggled = null;
        EmptyFolderRequested = null;
        SelectionModeToggled = null;
        FilterMenuRequested = null;
    }

    private void SelectAllChanged()
    {
        if (_applying) return;
        // GTK leaves the inconsistent flag set on a click.
        var wasMixed = _selectAll.GetInconsistent();
        _selectAll.SetInconsistent(false);
        var selectAll = wasMixed || _selectAll.GetActive();
        if (wasMixed && !_selectAll.GetActive())
        {
            _applying = true;
            try { _selectAll.SetActive(true); }
            finally { _applying = false; }
        }
        SelectAllToggled?.Invoke(selectAll);
    }
}
