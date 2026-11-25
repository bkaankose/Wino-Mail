using Wino.Mail.Linux.Views.Calendar;
using Wino.Presentation.Linux;

namespace Wino.Mail.Linux.Views.Shell;

/// <param name="Leading">Shown before the texts (a contact picture); owned by the list once shown, so pass a new one per Show.</param>
/// <param name="DotColorHex">A colour dot instead of a leading widget.</param>
internal sealed record SearchSuggestion(string Title, string? Subtitle, object? Tag, Gtk.Widget? Leading = null, string? DotColorHex = null);

/// <summary>
/// Suggestions under a search field (macOS ShellSearchSuggestionList): a popover that neither hides on outside clicks
/// nor takes the focus, so the field keeps the caret while the keyboard moves through the rows. Its owner closes it.
/// Rows are buttons because AT-SPI cannot activate list box rows.
/// </summary>
internal sealed class SearchSuggestionList : IDisposable
{
    private const int RowHeight = 44;
    private const int VisibleRows = 6;
    private const string HighlightClass = "wino-suggestion-highlighted";

    private readonly int _minimumWidth;
    private readonly Gtk.Popover _popover = Gtk.Popover.New();
    private readonly Gtk.ScrolledWindow _scroller = Gtk.ScrolledWindow.New();
    private readonly Gtk.Box _rows = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
    private readonly List<(Gtk.Button Row, SearchSuggestion Suggestion)> _items = [];
    private readonly List<IDisposable> _rowSubscriptions = [];
    private Gtk.Widget? _anchor;
    private IDisposable? _windowActive;
    private int _highlighted = -1;
    private bool _disposed;

    public SearchSuggestionList(int minimumWidth = 320)
    {
        WinoLiveObjects.Track(this);
        _minimumWidth = minimumWidth;
        _popover.SetAutohide(false);
        _popover.SetHasArrow(false);
        _popover.SetPosition(Gtk.PositionType.Bottom);
        _popover.SetHalign(Gtk.Align.Start);
        _popover.SetCanFocus(false);
        _popover.AddCssClass("menu");
        _scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
        _scroller.SetPropagateNaturalHeight(true);
        _scroller.SetMaxContentHeight(VisibleRows * RowHeight);
        _scroller.SetChild(_rows);
        _popover.SetChild(_scroller);
    }

    public event Action<SearchSuggestion>? Chosen;
    public event Action? Closed;

    public bool IsVisible => _popover.GetVisible();

    public SearchSuggestion? Highlighted => _highlighted >= 0 && _highlighted < _items.Count ? _items[_highlighted].Suggestion : null;

    public int Count => _items.Count;

    /// <summary>Replaces the rows and shows the list under <paramref name="anchor"/>; an empty array closes it.</summary>
    public void Show(Gtk.Widget anchor, SearchSuggestion[] suggestions)
    {
        if (_disposed) return;
        if (suggestions.Length == 0 || !anchor.GetMapped())
        {
            Close();
            return;
        }
        if (!ReferenceEquals(_anchor, anchor))
        {
            Close();
            if (_anchor is not null) _popover.Unparent();
            _popover.SetParent(anchor);
            _anchor = anchor;
        }
        Rebuild(suggestions);
        _scroller.SetSizeRequest(Math.Max(_minimumWidth, anchor.GetWidth()), -1);
        WatchWindow(anchor);
        if (!_popover.GetVisible()) _popover.Popup();
    }

    /// <summary>Moves the highlight and wraps; false while the list is closed.</summary>
    public bool Move(int delta)
    {
        if (!IsVisible || _items.Count == 0) return false;
        var next = _highlighted < 0
            ? (delta > 0 ? 0 : _items.Count - 1)
            : ((_highlighted + delta) % _items.Count + _items.Count) % _items.Count;
        SetHighlight(next);
        return true;
    }

    /// <summary>Chooses the highlighted row; false while the list is closed or nothing is highlighted.</summary>
    public bool TryChoose()
    {
        if (!IsVisible || Highlighted is not { } suggestion) return false;
        Choose(suggestion);
        return true;
    }

    public void Close()
    {
        _windowActive?.Dispose();
        _windowActive = null;
        if (!_popover.GetVisible()) return;
        _popover.Popdown();
        SetHighlight(-1);
        Closed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Close();
        _disposed = true;
        Chosen = null;
        Closed = null;
        ClearRows();
        if (_anchor is not null) _popover.Unparent();
        _anchor = null;
    }

#if DEBUG
    internal string Describe()
        => $"visible={IsVisible} rows={_items.Count} highlighted={_highlighted} [{string.Join(" | ", _items.Select(static item => item.Suggestion.Title))}]";

    internal Gtk.Popover DebugPopover => _popover;

    internal void DebugChoose(int index)
    {
        if (index >= 0 && index < _items.Count) _items[index].Row.Activate();
    }
#endif

    private void Rebuild(SearchSuggestion[] suggestions)
    {
        ClearRows();
        foreach (var suggestion in suggestions)
        {
            var row = Gtk.Button.New();
            row.AddCssClass("flat");
            row.AddCssClass("wino-suggestion");
            row.SetCanFocus(false);
            row.SetFocusOnClick(false);
            row.SetChild(RowContent(suggestion));
            WinoA11y.Rename(row, suggestion.Subtitle is { Length: > 0 } subtitle ? $"{suggestion.Title}, {subtitle}" : suggestion.Title);
            var captured = suggestion;
            _rowSubscriptions.Add(GtkSignals.OnClicked(row, () => Choose(captured)));
            _rows.Append(row);
            _items.Add((row, suggestion));
        }
        _highlighted = -1;
    }

    private static Gtk.Widget RowContent(SearchSuggestion suggestion)
    {
        var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
        box.SetSizeRequest(-1, suggestion.Subtitle is null ? 28 : 40);
        if (suggestion.Leading is { } leading)
        {
            leading.SetValign(Gtk.Align.Center);
            box.Append(leading);
        }
        else if (suggestion.DotColorHex is not null)
        {
            var dot = new CalendarColorDot();
            dot.SetColor(suggestion.DotColorHex, null);
            box.Append(dot.Root);
        }
        var texts = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
        texts.SetValign(Gtk.Align.Center);
        texts.SetHexpand(true);
        var title = Gtk.Label.New(suggestion.Title);
        title.SetXalign(0);
        title.SetEllipsize(Pango.EllipsizeMode.End);
        texts.Append(title);
        if (suggestion.Subtitle is { Length: > 0 } subtitle)
        {
            var caption = Gtk.Label.New(subtitle);
            caption.SetXalign(0);
            caption.SetEllipsize(Pango.EllipsizeMode.End);
            caption.AddCssClass("caption");
            caption.AddCssClass("dim-label");
            texts.Append(caption);
        }
        box.Append(texts);
        return box;
    }

    private void Choose(SearchSuggestion suggestion)
    {
        Close();
        Chosen?.Invoke(suggestion);
    }

    private void SetHighlight(int index)
    {
        if (_highlighted >= 0 && _highlighted < _items.Count) _items[_highlighted].Row.RemoveCssClass(HighlightClass);
        _highlighted = index;
        if (index < 0 || index >= _items.Count) return;
        var row = _items[index].Row;
        row.AddCssClass(HighlightClass);
        if (!row.ComputeBounds(_rows, out var bounds)) return;
        var adjustment = _scroller.GetVadjustment();
        var top = bounds.GetY();
        var bottom = top + bounds.GetHeight();
        if (top < adjustment.GetValue()) adjustment.SetValue(top);
        else if (bottom > adjustment.GetValue() + adjustment.GetPageSize()) adjustment.SetValue(bottom - adjustment.GetPageSize());
    }

    private void WatchWindow(Gtk.Widget anchor)
    {
        if (_windowActive is not null || anchor.GetRoot() is not Gtk.Window window) return;
        _windowActive = GtkSignals.OnNotify(window, "is-active", () =>
        {
            if (!window.IsActive) Close();
        });
    }

    private void ClearRows()
    {
        foreach (var subscription in _rowSubscriptions) subscription.Dispose();
        _rowSubscriptions.Clear();
        while (_rows.GetFirstChild() is { } child) _rows.Remove(child);
        _items.Clear();
    }
}
