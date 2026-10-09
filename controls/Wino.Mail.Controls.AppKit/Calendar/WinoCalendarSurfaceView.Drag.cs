using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Core.Domain.Interfaces;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Calendar;

/// <summary>A tile was dropped at a new start (duration kept).</summary>
public sealed class CalendarItemMoveRequestedEventArgs(ICalendarItem item, DateTime start) : EventArgs
{
    public ICalendarItem Item { get; } = item;
    public DateTime Start { get; } = start;
}

/// <summary>A timed tile's bottom edge was dragged to a new end (start kept).</summary>
public sealed class CalendarItemResizeRequestedEventArgs(ICalendarItem item, DateTime end) : EventArgs
{
    public ICalendarItem Item { get; } = item;
    public DateTime End { get; } = end;
}

/// <summary>Where a drag currently lands: the snapped times and the ghost frame in host coordinates.</summary>
internal readonly record struct DragTarget(DateTime Start, DateTime End, CGRect Frame);

/// <summary>One tile drag in progress.</summary>
internal sealed class DragSession(WinoCalendarSurfaceView.TileHostView host, WinoCalendarItemView tile, ICalendarItem item, bool resize, CGPoint downPoint)
{
    public WinoCalendarSurfaceView.TileHostView Host { get; } = host;
    public WinoCalendarItemView Tile { get; } = tile;
    public ICalendarItem Item { get; } = item;
    public bool Resize { get; } = resize;
    public CGPoint DownPoint { get; } = downPoint;
    public CGRect OriginFrame { get; } = tile.Frame;
    public DateTime OriginalStart { get; } = item.StartDate;
    public DateTime OriginalEnd { get; } = item.EndDate;
    public DragTarget? Target { get; set; }
    public WinoCalendarDragGhostView? Ghost { get; set; }
}

/// <summary>
/// Tile drag on the surface (Windows CalendarItemControl drag + CalendarPeriodControl drop, plus a
/// Mac bottom-edge resize). The press stays with the tile view, which forwards the drag here; the
/// surface draws a ghost tile at the snapped target with a live time chip and raises
/// <see cref="ItemMoveRequested"/> or <see cref="ItemResizeRequested"/> on release. Items the page
/// refuses (<see cref="CanDragItem"/>) raise <see cref="ItemDragRefused"/> instead and never move.
/// Timed tiles stay in the hour grid, all-day tiles in the all-day strip, month tiles in the month grid.
/// </summary>
public sealed partial class WinoCalendarSurfaceView
{
    private DragSession? _drag;

    /// <summary>False refuses the drag (read-only calendar, locked or busy event). Null allows every item.</summary>
    public Func<ICalendarItem, bool>? CanDragItem { get; set; }

    /// <summary>Drop granularity in minutes (Windows TimedSelectionIntervalMinutes).</summary>
    public int DragSnapMinutes { get; set; } = 15;

    public event EventHandler<CalendarItemClickedEventArgs>? ItemDragRefused;
    public event EventHandler<CalendarItemMoveRequestedEventArgs>? ItemMoveRequested;
    public event EventHandler<CalendarItemResizeRequestedEventArgs>? ItemResizeRequested;

    /// <summary>Lays the tiles out again, for example after an item's times changed in place.</summary>
    public void ReloadLayout() => Invalidate();

    /// <summary>True while a tile drag (or a debug preview) shows its ghost.</summary>
    public bool IsDraggingItem => _drag is not null;

    /// <summary>The target of the drag in progress, for diagnostics.</summary>
    public (DateTime Start, DateTime End)? CurrentDragTarget => _drag?.Target is { } target ? (target.Start, target.End) : null;

    internal bool AllowsResize(ICalendarItem item, CalendarPlacementKind kind)
        => kind == CalendarPlacementKind.Timed && !item.IsMultiDayEvent && !item.IsAllDayEvent && CanDragItem?.Invoke(item) != false;

    internal DateTime Snap(DateTime value)
    {
        long interval = TimeSpan.FromMinutes(Math.Max(1, DragSnapMinutes)).Ticks;
        return new DateTime((value.Ticks + interval / 2) / interval * interval, value.Kind);
    }

    internal void TileRecycled(WinoCalendarItemView tile)
    {
        if (_drag is { } drag && ReferenceEquals(drag.Tile, tile)) EndTileDrag(commit: false);
    }

    internal void BeginTileDrag(TileHostView host, WinoCalendarItemView tile, CGPoint downPoint, NSEvent? press, bool resize)
    {
        EndTileDrag(commit: false);
        if (tile.Item is not ICalendarItem item || tile.Kind == CalendarPlacementKind.MultiDayGhost) return;
        if (CanDragItem?.Invoke(item) == false)
        {
            ItemDragRefused?.Invoke(this, new CalendarItemClickedEventArgs(item, tile, press));
            return;
        }

        var session = new DragSession(host, tile, item, resize && AllowsResize(item, tile.Kind), downPoint);
        var ghost = new WinoCalendarDragGhostView(tile.Model, tile.Kind);
        session.Ghost = ghost;
        host.AddSubview(ghost, NSWindowOrderingMode.Above, null);
        tile.AlphaValue = 0.4f;
        _drag = session;
        UpdateDrag(downPoint);
    }

    internal void MoveTileDrag(NSEvent native)
    {
        if (_drag is not { } drag) return;
        if (!ReferenceEquals(drag.Tile.Item, drag.Item)) { EndTileDrag(commit: false); return; }
        drag.Host.Autoscroll(native);
        UpdateDrag(drag.Host.ConvertPointFromView(native.LocationInWindow, null));
    }

    internal void EndTileDrag(bool commit)
    {
        if (_drag is not { } drag) return;
        _drag = null;
        drag.Tile.AlphaValue = 1;
        drag.Ghost?.RemoveFromSuperview();
        drag.Ghost?.Dispose();
        if (!commit || drag.Target is not { } target) return;

        if (drag.Resize)
        {
            if (target.End != drag.OriginalEnd) ItemResizeRequested?.Invoke(this, new CalendarItemResizeRequestedEventArgs(drag.Item, target.End));
        }
        else if (target.Start != drag.OriginalStart)
        {
            ItemMoveRequested?.Invoke(this, new CalendarItemMoveRequestedEventArgs(drag.Item, target.Start));
        }
    }

    private void UpdateDrag(CGPoint point)
    {
        if (_drag is not { } drag || drag.Ghost is not { } ghost) return;
        var target = drag.Host.ResolveDrag(drag, point) ?? drag.Target;
        if (target is not { } resolved) return;
        drag.Target = resolved;
        ghost.Frame = resolved.Frame;
        var time = drag.Resize ? resolved.End : resolved.Start;
        ghost.SetTimeText(_settings is null ? time.ToString("t") : _settings.GetTimeString(time.TimeOfDay), !drag.Resize && drag.Tile.Kind != CalendarPlacementKind.Timed);
    }

    /// <summary>
    /// Shows a drag of <paramref name="item"/> by <paramref name="days"/> and <paramref name="minutes"/>
    /// (a bottom-edge resize when <paramref name="resize"/> is true) without a pointer, for checks and
    /// screenshots. Finish it with <see cref="FinishPreviewDrag"/>. False when the item has no tile on screen.
    /// </summary>
    public bool PreviewDrag(ICalendarItem item, int days, double minutes, bool resize)
    {
        EndTileDrag(commit: false);
        TileHostView host = IsMonth ? _month : item.IsAllDayEvent ? _allDay : _timed;
        if (host.FindTile(item) is not { } tile) return false;
        var frame = tile.Frame;
        var down = new CGPoint(frame.GetMidX(), resize ? frame.GetMaxY() - 2 : frame.GetMidY());
        BeginTileDrag(host, tile, down, null, resize);
        if (_drag is null) return true;
        var offset = host.PreviewOffset(days, minutes);
        UpdateDrag(new CGPoint(down.X + offset.Width, down.Y + offset.Height));
        if (_drag?.Ghost is { } ghost) host.ScrollRectToVisible(ghost.Frame.Inset(0, -24));
        return true;
    }

    /// <summary>Ends a preview drag, raising the move or resize request when <paramref name="commit"/> is true.</summary>
    public void FinishPreviewDrag(bool commit) => EndTileDrag(commit);
}

/// <summary>
/// The tile drawn at a drag target: the tile at full opacity with a 2pt white outline and a shadow,
/// and a small dark chip with the snapped start (or, for a resize, end) time at its top.
/// </summary>
public sealed class WinoCalendarDragGhostView : NSView
{
    private readonly WinoCalendarItemView _tile = new();
    private readonly TimeChip _chip = new();

    internal WinoCalendarDragGhostView(CalendarTileModel? model, CalendarPlacementKind kind)
    {
        WantsLayer = true;
        _tile.Model = model is null ? null : model with { IsSelected = false, IsBusy = false };
        _tile.Kind = kind;
        _tile.WantsLayer = true;
        _tile.Layer!.BorderColor = NSColor.White.CGColor;
        _tile.Layer.BorderWidth = 2;
        _tile.Layer.CornerRadius = 3;
        _tile.Shadow = new NSShadow { ShadowColor = NSColor.Black.ColorWithAlphaComponent(0.3f), ShadowBlurRadius = 8, ShadowOffset = new CGSize(0, -2) };
        AddSubview(_tile);
        AddSubview(_chip);
    }

    public override bool IsFlipped => true;

    /// <summary>The ghost never takes the pointer; the dragged tile keeps tracking it.</summary>
    public override NSView? HitTest(CGPoint point) => null;

    internal void SetTimeText(string text, bool hidden)
    {
        _chip.Text = text;
        _chip.Hidden = hidden || string.IsNullOrEmpty(text);
        NeedsLayout = true;
    }

    public override void Layout()
    {
        base.Layout();
        _tile.Frame = Bounds;
        var size = _chip.FittingSize;
        // The chip sits inside the tile's top-right corner so it never leaves the canvas.
        _chip.Frame = new CGRect(Math.Max(0, Bounds.Width - size.Width - 4), 3, size.Width, size.Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tile.Dispose();
        base.Dispose(disposing);
    }

    private sealed class TimeChip : NSView
    {
        private static readonly NSFont Font = NSFont.SystemFontOfSize(10, NSFontWeight.Semibold);
        private string _text = string.Empty;

        public string Text
        {
            get => _text;
            set { _text = value; NeedsDisplay = true; InvalidateIntrinsicContentSize(); }
        }

        public override bool IsFlipped => true;

        public override CGSize FittingSize
        {
            get
            {
                var size = new NSAttributedString(_text, new NSStringAttributes { Font = Font }).Size;
                return new CGSize(Math.Ceiling(size.Width) + 10, Math.Ceiling(size.Height) + 2);
            }
        }

        public override void DrawRect(CGRect dirtyRect)
        {
            WinoStyle.Hex(0x000000, 0.78).SetFill();
            NSBezierPath.FromRoundedRect(Bounds, 4, 4).Fill();
            var attributed = new NSAttributedString(_text, new NSStringAttributes { Font = Font, ForegroundColor = NSColor.White });
            var size = attributed.Size;
            attributed.DrawAtPoint(new CGPoint((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2));
        }
    }
}
