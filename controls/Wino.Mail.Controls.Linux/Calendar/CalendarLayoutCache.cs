using Wino.Presentation.Controls;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Calendar;

internal enum CalendarTextStyle
{
    DayHeader,
    HourLabel,
    AllDayLabel,
    TileTitle13,
    TileTitle12,
    TileTitle11,
    TileTitleMonth,
    TileDetail,
    TileTime,
    Glyph,
    MoreLink,
    MonthDay,
    MonthDayToday,
    MonthWeekday
}

/// <summary>
/// The surface's text layouts, created once per text, style and width and pinned while cached, so no draw function
/// creates a layout. Bounded: past <see cref="Capacity"/> entries the least recently used quarter is dropped.
/// </summary>
internal sealed class CalendarLayoutCache : IDisposable
{
    private const int Capacity = 600;
    private const int Evicted = 150;

    private readonly Gtk.Widget _context;
    private readonly Dictionary<Key, Entry> _entries = [];
    private readonly Pango.FontDescription?[] _fonts = new Pango.FontDescription?[(int)CalendarTextStyle.MonthWeekday + 1];
    private long _tick;
    private bool _disposed;

    public CalendarLayoutCache(Gtk.Widget context) => _context = context;

    public int Count => _entries.Count;

    public PinnedLayout Get(string text, CalendarTextStyle style, int widthPixels = -1)
    {
        var key = new Key(text, style, widthPixels);
        if (_entries.TryGetValue(key, out var entry))
        {
            entry.Tick = ++_tick;
            return entry.Layout;
        }
        if (_entries.Count >= Capacity) Evict();
        var layout = _context.CreatePangoLayout(text);
        layout.SetFontDescription(Font(style));
        if (widthPixels >= 0)
        {
            layout.SetWidth(Math.Max(1, widthPixels) * Pango.Constants.SCALE);
            layout.SetEllipsize(Pango.EllipsizeMode.End);
            layout.SetSingleParagraphMode(true);
        }
        var pinned = new PinnedLayout(layout);
        _entries[key] = new Entry(pinned) { Tick = ++_tick };
        return pinned;
    }

    /// <summary>Drops every layout and font (scale, font or text direction changed).</summary>
    public void Clear()
    {
        foreach (var entry in _entries.Values) entry.Layout.Dispose();
        _entries.Clear();
        Array.Clear(_fonts);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
    }

    private void Evict()
    {
        var oldest = new List<KeyValuePair<Key, Entry>>(_entries);
        oldest.Sort(static (a, b) => a.Value.Tick.CompareTo(b.Value.Tick));
        for (var index = 0; index < Evicted && index < oldest.Count; index++)
        {
            oldest[index].Value.Layout.Dispose();
            _entries.Remove(oldest[index].Key);
        }
    }

    private Pango.FontDescription Font(CalendarTextStyle style)
    {
        if (_fonts[(int)style] is { } known) return known;
        var font = _context.GetPangoContext().GetFontDescription()?.Copy() ?? Pango.FontDescription.New();
        var (size, weight) = style switch
        {
            CalendarTextStyle.DayHeader => (13, Pango.Weight.Semibold),
            CalendarTextStyle.HourLabel or CalendarTextStyle.AllDayLabel or CalendarTextStyle.TileDetail or CalendarTextStyle.TileTime => (11, Pango.Weight.Normal),
            CalendarTextStyle.TileTitle13 => (13, Pango.Weight.Semibold),
            CalendarTextStyle.TileTitle12 => (12, Pango.Weight.Semibold),
            CalendarTextStyle.TileTitle11 => (11, Pango.Weight.Semibold),
            CalendarTextStyle.TileTitleMonth => (12, Pango.Weight.Medium),
            CalendarTextStyle.MoreLink => (11, Pango.Weight.Semibold),
            CalendarTextStyle.MonthDay => (12, Pango.Weight.Medium),
            CalendarTextStyle.MonthDayToday or CalendarTextStyle.MonthWeekday => (12, Pango.Weight.Semibold),
            CalendarTextStyle.Glyph => ((int)WinoCalendarTileAppearance.GlyphSize, Pango.Weight.Normal),
            _ => (12, Pango.Weight.Normal)
        };
        if (style == CalendarTextStyle.Glyph) font.SetFamily(WinoIcons.SetFamily);
        font.SetAbsoluteSize(size * Pango.Constants.SCALE);
        font.SetWeight(weight);
        _fonts[(int)style] = font;
        return font;
    }

    private readonly record struct Key(string Text, CalendarTextStyle Style, int Width);

    private sealed class Entry(PinnedLayout layout)
    {
        public PinnedLayout Layout { get; } = layout;
        public long Tick { get; set; }
    }
}
