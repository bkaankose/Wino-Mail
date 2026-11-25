using System.Globalization;
using System.Runtime.CompilerServices;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Calendar;

/// <summary>
/// The tiles of one strip in a <see cref="Gtk.Fixed"/> overlay (macOS TileHostView): active tiles keyed by item and
/// day, and at most 64 spare ones for reuse. Tiles sit at rail coordinates minus the layer origin in an inner content
/// fixed that the rail offset moves as a whole, so a frame of motion moves one widget instead of every tile; the
/// "n more" links are pooled flat buttons in the same content.
/// </summary>
internal sealed class CalendarTileLayer : IDisposable
{
    private const int SpareLimit = 64;

    private readonly Func<WinoCalendarTile> _create;
    private readonly Action<WinoCalendarTile> _recycled;
    private readonly Dictionary<(ICalendarItem Item, DateOnly Date), WinoCalendarTile> _active = new(KeyComparer.Instance);
    private readonly Stack<WinoCalendarTile> _spare = new();
    private readonly HashSet<(ICalendarItem, DateOnly)> _seen = new(KeyComparer.Instance);
    private readonly List<(ICalendarItem, DateOnly)> _stale = [];
    private readonly List<Gtk.Button> _moreButtons = [];
    private readonly List<CalendarLayoutOverflowCell> _overflows = [];
    private readonly List<IDisposable> _moreSubscriptions = [];
    private Action<CalendarLayoutOverflowCell, Gtk.Button>? _moreClicked;
    private double _offsetX = double.NaN;
    private double _offsetY = double.NaN;
    private double _scale = 1;
    private int _usedMore;

    public CalendarTileLayer(CalendarStrip strip, double originX, Func<WinoCalendarTile> create, Action<WinoCalendarTile> recycled)
    {
        Strip = strip;
        OriginX = originX;
        _create = create;
        _recycled = recycled;
        Fixed = Gtk.Fixed.New();
        Fixed.SetOverflow(Gtk.Overflow.Hidden);
        Fixed.SetMarginStart((int)originX);
        Fixed.SetHalign(Gtk.Align.Fill);
        Fixed.SetValign(Gtk.Align.Fill);
        Content = Gtk.Fixed.New();
        Fixed.Put(Content, 0, 0);
    }

    /// <summary>The moving child of <see cref="Fixed"/> that holds the tiles, the links and any rail content a strip adds below them.</summary>
    public Gtk.Fixed Content { get; }

    public Gtk.Fixed Fixed { get; }

    public CalendarStrip Strip { get; }

    public double OriginX { get; }

    public int ActiveCount => _active.Count;

    public int SpareCount => _spare.Count;

    public IReadOnlyCollection<WinoCalendarTile> Active => _active.Values;

    public int OverflowCount => _usedMore;

    public IReadOnlyList<CalendarLayoutOverflowCell> Overflows => _overflows;

    public void Sync(List<CalendarLayoutPlacement> placements, Func<ICalendarItem, DateOnly, CalendarTileModel> model, Func<ICalendarItem, DateOnly, string>? name)
    {
        _seen.Clear();
        for (var index = 0; index < placements.Count; index++)
        {
            var placement = placements[index];
            var key = (placement.Item, placement.Date);
            if (!_seen.Add(key)) continue;
            if (!_active.TryGetValue(key, out var tile))
            {
                tile = _spare.Count > 0 ? _spare.Pop() : Create();
                tile.Root.SetVisible(true);
                _active[key] = tile;
            }
            tile.Item = placement.Item;
            tile.Kind = placement.Kind;
            tile.Date = placement.Date;
            tile.Model = model(placement.Item, placement.Date);
            tile.SetFrame(placement.Bounds);
            tile.SetAccessibleName(name?.Invoke(placement.Item, placement.Date));
            Place(tile);
        }

        _stale.Clear();
        foreach (var key in _active.Keys) if (!_seen.Contains(key)) _stale.Add(key);
        foreach (var key in _stale)
        {
            if (!_active.Remove(key, out var tile)) continue;
            Recycle(tile);
        }
    }

    public void SyncOverflows(List<CalendarLayoutOverflowCell> cells, Action<CalendarLayoutOverflowCell, Gtk.Button> clicked, CultureInfo? culture)
    {
        _moreClicked = clicked;
        _overflows.Clear();
        _overflows.AddRange(cells);
        _usedMore = cells.Count;
        while (_moreButtons.Count < cells.Count)
        {
            var button = Gtk.Button.New();
            button.AddCssClass("flat");
            button.AddCssClass("wino-calendar-more");
            var label = Gtk.Label.New(string.Empty);
            label.SetXalign(0);
            // A fixed gives its child the natural size: a one-character natural width lets the size request decide, so a
            // long link ellipsizes inside its day instead of running over the next one.
            label.SetEllipsize(Pango.EllipsizeMode.End);
            label.SetMaxWidthChars(1);
            button.SetChild(label);
            var slot = _moreButtons.Count;
            _moreSubscriptions.Add(GtkSignals.OnClicked(button, () =>
            {
                if (slot < _usedMore) _moreClicked?.Invoke(_overflows[slot], button);
            }));
            Content.Put(button, 0, 0);
            _moreButtons.Add(button);
        }
        for (var index = 0; index < _moreButtons.Count; index++)
        {
            var button = _moreButtons[index];
            if (index >= cells.Count)
            {
                button.SetVisible(false);
                continue;
            }
            var cell = cells[index];
            if (button.GetChild() is Gtk.Label label) label.SetText(cell.Text);
            WinoA11y.Label(button, CalendarItemOverflow.FormatAccessibleName(cell.HiddenItems.Count, cell.Date, culture ?? CultureInfo.CurrentCulture));
            button.SetSizeRequest(Math.Max(1, (int)Math.Round(cell.Bounds.Width)), Math.Max(1, (int)Math.Round(cell.Bounds.Height)));
            button.SetVisible(true);
            PlaceMore(button, cell.Bounds);
        }
    }

    public void RefreshModels(Func<ICalendarItem, DateOnly, CalendarTileModel> model)
    {
        foreach (var (key, tile) in _active) tile.Model = model(key.Item, key.Date);
    }

    /// <summary>Moves the content by the rail offset; nothing happens while the device-pixel offset stays the same.</summary>
    public void SetOffset(double offsetX, double offsetY, double scale)
    {
        var newScale = scale <= 0 ? 1 : scale;
        offsetX = Math.Round(offsetX * newScale) / newScale;
        offsetY = Math.Round(offsetY * newScale) / newScale;
        if (offsetX == _offsetX && offsetY == _offsetY && newScale == _scale) return;
        if (newScale != _scale)
        {
            _scale = newScale;
            foreach (var tile in _active.Values) Place(tile);
            for (var index = 0; index < _usedMore && index < _moreButtons.Count; index++) PlaceMore(_moreButtons[index], _overflows[index].Bounds);
        }
        _offsetX = offsetX;
        _offsetY = offsetY;
        Fixed.Move(Content, -offsetX, -offsetY);
    }

    public void Clear()
    {
        foreach (var tile in _active.Values) Recycle(tile);
        _active.Clear();
        _overflows.Clear();
        _usedMore = 0;
        foreach (var button in _moreButtons) button.SetVisible(false);
    }

    public WinoCalendarTile? Find(ICalendarItem item)
    {
        foreach (var (key, tile) in _active)
            if (ReferenceEquals(key.Item, item)) return tile;
        return null;
    }

    public bool Shows(ICalendarItem item)
    {
        foreach (var key in _active.Keys) if (ReferenceEquals(key.Item, item)) return true;
        return false;
    }

    public void Dispose()
    {
        foreach (var subscription in _moreSubscriptions) subscription.Dispose();
        _moreSubscriptions.Clear();
        _moreClicked = null;
        foreach (var tile in _active.Values)
        {
            _recycled(tile);
            tile.Release();
            Content.Remove(tile.Root);
        }
        _active.Clear();
        while (_spare.Count > 0)
        {
            var tile = _spare.Pop();
            tile.Release();
            Content.Remove(tile.Root);
        }
        foreach (var button in _moreButtons) Content.Remove(button);
        _moreButtons.Clear();
    }

    private WinoCalendarTile Create()
    {
        var tile = _create();
        Content.Put(tile.Root, 0, 0);
        return tile;
    }

    private void Recycle(WinoCalendarTile tile)
    {
        _recycled(tile);
        tile.Detach();
        if (_spare.Count < SpareLimit) _spare.Push(tile);
        else
        {
            tile.Release();
            Content.Remove(tile.Root);
        }
    }

    private void Place(WinoCalendarTile tile)
    {
        var frame = tile.Frame;
        Content.Move(tile.Root, Math.Round((frame.X - OriginX) * _scale) / _scale, Math.Round(frame.Y * _scale) / _scale);
    }

    private void PlaceMore(Gtk.Button button, CalendarLayoutRect bounds)
        => Content.Move(button, Math.Round((bounds.X - OriginX) * _scale) / _scale, Math.Round(bounds.Y * _scale) / _scale);

    private sealed class KeyComparer : IEqualityComparer<(ICalendarItem Item, DateOnly Date)>
    {
        public static KeyComparer Instance { get; } = new();

        public bool Equals((ICalendarItem Item, DateOnly Date) x, (ICalendarItem Item, DateOnly Date) y) => ReferenceEquals(x.Item, y.Item) && x.Date == y.Date;

        public int GetHashCode((ICalendarItem Item, DateOnly Date) obj) => HashCode.Combine(RuntimeHelpers.GetHashCode(obj.Item), obj.Date);
    }
}
