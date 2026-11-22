using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Presentation;

namespace Wino.Mail.Controls.Linux.Calendar;

/// <summary>What one event tile shows. The page builds it from the shared calendar item ViewModel.</summary>
public sealed record CalendarTileModel(string Title, string? TimeRange, string? Location, WinoRgba Fill, CalendarItemShowAs ShowAs,
    bool IsRecurring, bool IsMultiDay, bool IsSelected, bool IsBusy, bool IsAllDay = false, bool IsCancelled = false, bool NeedsResponse = false, bool IsPast = false);

/// <summary>An event tile was pressed; X and Y are the pointer in tile coordinates (the tile centre for the keyboard).</summary>
public sealed class CalendarItemEventArgs(ICalendarItem item, WinoCalendarTile tile, double x, double y) : EventArgs
{
    public ICalendarItem Item { get; } = item;
    public WinoCalendarTile Tile { get; } = tile;
    public double X { get; } = x;
    public double Y { get; } = y;
}

/// <summary>An empty slot was clicked, or a range was dragged (End, exclusive). Anchor is in AnchorWidget coordinates.</summary>
public sealed class CalendarSlotEventArgs(DateTime start, DateTime? end, bool isAllDay, Gtk.Widget anchorWidget, Gdk.Rectangle anchor) : EventArgs
{
    public DateTime Start { get; } = start;
    public DateTime? End { get; } = end;
    public bool IsAllDay { get; } = isAllDay;
    public Gtk.Widget AnchorWidget { get; } = anchorWidget;
    public Gdk.Rectangle Anchor { get; } = anchor;
}

/// <summary>A period on the rail: the anchor date resolves to the range that starts on the unit the rail rests on.</summary>
public sealed class CalendarPeriodEventArgs(CalendarDisplayType displayType, DateOnly anchorDate) : EventArgs
{
    public CalendarDisplayType DisplayType { get; } = displayType;
    public DateOnly AnchorDate { get; } = anchorDate;
}

internal enum CalendarStrip
{
    Header,
    AllDay,
    Timed,
    Month
}

internal struct CalendarColor(double r, double g, double b, double a = 1)
{
    public double R = r, G = g, B = b, A = a;

    public static CalendarColor From(WinoRgba rgba, double alpha = 1) => new(rgba.R / 255.0, rgba.G / 255.0, rgba.B / 255.0, rgba.Alpha * alpha);

    public readonly CalendarColor WithAlpha(double alpha) => new(R, G, B, alpha);

    /// <summary><paramref name="share"/> of <paramref name="other"/> blended into this colour (NSColor.blendedColor).</summary>
    public readonly CalendarColor Blend(double share, CalendarColor other)
        => new(R + (other.R - R) * share, G + (other.G - G) * share, B + (other.B - B) * share, A + (other.A - A) * share);

    public readonly void Use(Cairo.Context cr) => cr.SetSourceRgba(R, G, B, A);
}
