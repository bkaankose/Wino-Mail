using System.Globalization;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Mail.Controls.Core.Calendar;
using Wino.Mail.Controls.Core.Layout;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Calendar;

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

/// <summary>
/// Tile drag on the surface (macOS WinoCalendarSurfaceView.Drag, Windows CalendarItemControl drag and drop): a press on
/// a tile that moves <see cref="WinoCalendarSurfaceGeometry.TileDragThreshold"/> shows a ghost tile at the snapped
/// target and dims the tile; release raises <see cref="ItemMoveRequested"/> or, for a press on the bottom edge of a
/// timed tile, <see cref="ItemResizeRequested"/>. Items the page refuses (<see cref="CanDragItem"/>) raise
/// <see cref="ItemDragRefused"/> and never move. Timed tiles stay in the hour grid, all-day tiles in the all-day strip
/// and month tiles in the month grid, as on both references.
/// </summary>
public sealed partial class WinoCalendarSurface
{
    private const double OriginOpacity = 0.35;

    private DragSession? _drag;
    private bool _tileDragActive;

    private sealed class DragSession(CalendarStrip strip, CalendarTileLayer layer, WinoCalendarTile tile, ICalendarItem item, bool resize, double downX, double downY)
    {
        public CalendarStrip Strip { get; } = strip;
        public CalendarTileLayer Layer { get; } = layer;
        public WinoCalendarTile Tile { get; } = tile;
        public ICalendarItem Item { get; } = item;
        public bool Resize { get; } = resize;

        /// <summary>The press in rail coordinates.</summary>
        public double DownX { get; } = downX;
        public double DownY { get; } = downY;
        public WinoLayoutFrame OriginFrame { get; } = new(tile.Frame.X, tile.Frame.Y, tile.Frame.Width, tile.Frame.Height);
        public DateTime OriginalStart { get; } = item.StartDate;
        public DateTime OriginalEnd { get; } = item.EndDate;
        public WinoCalendarDragTarget? Target { get; set; }
        public WinoCalendarTile? Ghost { get; set; }
    }

    /// <summary>False refuses the drag (read-only calendar, locked or busy event). Null allows every item.</summary>
    public Func<ICalendarItem, bool>? CanDragItem { get; set; }

    /// <summary>Drop granularity in minutes (Windows TimedSelectionIntervalMinutes).</summary>
    public int DragSnapMinutes { get; set; } = 15;

    public event EventHandler<CalendarItemEventArgs>? ItemDragRefused;
    public event EventHandler<CalendarItemMoveRequestedEventArgs>? ItemMoveRequested;
    public event EventHandler<CalendarItemResizeRequestedEventArgs>? ItemResizeRequested;

    /// <summary>True while a tile drag (or a debug preview) shows its ghost.</summary>
    public bool IsDraggingItem => _drag is not null;

    /// <summary>The target of the drag in progress, for diagnostics.</summary>
    public (DateTime Start, DateTime End)? CurrentDragTarget => _drag?.Target is { } target ? (target.Start, target.End) : null;

    private CalendarTileLayer? LayerOf(CalendarStrip strip) => strip switch
    {
        CalendarStrip.Timed => _timedLayer,
        CalendarStrip.AllDay => _allDayLayer,
        CalendarStrip.Month => _monthLayer,
        _ => null
    };

    private bool AllowsResize(ICalendarItem item, CalendarPlacementKind kind)
        => kind == CalendarPlacementKind.Timed && !item.IsMultiDayEvent && !item.IsAllDayEvent && CanDragItem?.Invoke(item) != false;

    /// <summary>One drag gesture and one motion controller per tile, created once with the tile; the item is read when they fire.</summary>
    partial void OnTileRealized(WinoCalendarTile tile, CalendarStrip strip)
    {
        if (strip is not (CalendarStrip.Timed or CalendarStrip.AllDay or CalendarStrip.Month)) return;
        var root = tile.Root;
        var drag = Gtk.GestureDrag.New();
        drag.SetButton(1);
        root.AddController(drag);
        var surface = _ref;
        var pressedAtEdge = false;
        tile.Own(GtkSignals.Connect<Gtk.GestureDrag, Gtk.GestureDrag.DragBeginSignalArgs>(h => drag.OnDragBegin += h, h => drag.OnDragBegin -= h, (_, args) =>
        {
            tile.DragStarted = false;
            pressedAtEdge = args.StartY >= root.GetHeight() - WinoCalendarSurfaceGeometry.ResizeHandleHeight;
        }));
        tile.Own(GtkSignals.Connect<Gtk.GestureDrag, Gtk.GestureDrag.DragUpdateSignalArgs>(h => drag.OnDragUpdate += h, h => drag.OnDragUpdate -= h, (gesture, args) =>
        {
            if (surface.Value is not { } owner) return;
            if (!tile.DragStarted)
            {
                if (Math.Abs(args.OffsetX) < WinoCalendarSurfaceGeometry.TileDragThreshold && Math.Abs(args.OffsetY) < WinoCalendarSurfaceGeometry.TileDragThreshold) return;
                if (!gesture.GetStartPoint(out var startX, out var startY)) return;
                tile.DragStarted = true;
                if (!owner.BeginTileDrag(strip, tile, startX, startY, pressedAtEdge))
                {
                    gesture.SetState(Gtk.EventSequenceState.Denied);
                    return;
                }
                gesture.SetState(Gtk.EventSequenceState.Claimed);
            }
            if (gesture.GetStartPoint(out var x, out var y)) owner.MoveTileDrag(tile, x + args.OffsetX, y + args.OffsetY);
        }));
        tile.Own(GtkSignals.Connect<Gtk.GestureDrag, Gtk.GestureDrag.DragEndSignalArgs>(h => drag.OnDragEnd += h, h => drag.OnDragEnd -= h, (_, _) =>
        {
            if (surface.Value is { } owner && owner._drag is { } session && ReferenceEquals(session.Tile, tile)) owner.EndTileDrag(commit: true);
        }));
        tile.Own(GtkSignals.Connect<Gtk.Gesture, Gtk.Gesture.CancelSignalArgs>(h => drag.OnCancel += h, h => drag.OnCancel -= h, (_, _) =>
        {
            if (surface.Value is { } owner && owner._drag is { } session && ReferenceEquals(session.Tile, tile)) owner.EndTileDrag(commit: false);
        }));

        if (strip != CalendarStrip.Timed) return;
        var motion = Gtk.EventControllerMotion.New();
        root.AddController(motion);
        tile.Own(GtkSignals.Connect<Gtk.EventControllerMotion, Gtk.EventControllerMotion.MotionSignalArgs>(h => motion.OnMotion += h, h => motion.OnMotion -= h, (_, args) =>
        {
            var edge = tile.Item is { } item && surface.Value is { } owner && owner.AllowsResize(item, tile.Kind) &&
                       args.Y >= root.GetHeight() - WinoCalendarSurfaceGeometry.ResizeHandleHeight;
            root.SetCursorFromName(edge ? "ns-resize" : null);
        }));
    }

    partial void OnTileRecycled(WinoCalendarTile tile)
    {
        if (_drag is { } drag && ReferenceEquals(drag.Tile, tile)) EndTileDrag(commit: false);
    }

    /// <summary>Starts a drag at a point in tile coordinates; false when the tile cannot be dragged.</summary>
    private bool BeginTileDrag(CalendarStrip strip, WinoCalendarTile tile, double tileX, double tileY, bool atEdge)
    {
        EndTileDrag(commit: false);
        CancelPress();
        if (tile.Item is not { } item || LayerOf(strip) is not { } layer) return false;
        if (CanDragItem?.Invoke(item) == false)
        {
            ItemDragRefused?.Invoke(this, new CalendarItemEventArgs(item, tile, tileX, tileY));
            return false;
        }
        var frame = tile.Frame;
        var session = new DragSession(strip, layer, tile, item, atEdge && AllowsResize(item, tile.Kind), frame.X + tileX, frame.Y + tileY);
        var ghost = new WinoCalendarTile(_layouts, () => Palette)
        {
            Item = item,
            Kind = tile.Kind,
            Date = tile.Date
        };
        ghost.Model = tile.Model is { } model ? model with { IsSelected = false, IsBusy = false } : null;
        ghost.SetFrame(frame);
        ghost.SetDragAppearance(ghost: true);
        ghost.Root.SetCanTarget(false);
        layer.Content.Put(ghost.Root, 0, 0);
        session.Ghost = ghost;
        tile.Root.SetOpacity(OriginOpacity);
        _drag = session;
        _tileDragActive = true;
        UpdateDrag(session.DownX, session.DownY);
        return true;
    }

    /// <summary>The pointer moved to a point in tile coordinates of the dragged tile (which stays put while its ghost moves).</summary>
    private void MoveTileDrag(WinoCalendarTile tile, double tileX, double tileY)
    {
        if (_drag is not { } drag || !ReferenceEquals(drag.Tile, tile)) return;
        if (!ReferenceEquals(tile.Item, drag.Item))
        {
            EndTileDrag(commit: false);
            return;
        }
        var frame = tile.Frame;
        if (drag.Strip == CalendarStrip.Timed)
        {
            var (_, stripY) = ToStrip(CalendarStrip.Timed, frame.X + tileX, frame.Y + tileY);
            AutoScroll(stripY);
        }
        UpdateDrag(frame.X + tileX, frame.Y + tileY);
    }

    private void EndTileDrag(bool commit)
    {
        if (_drag is not { } drag) return;
        _drag = null;
        _tileDragActive = false;
        drag.Tile.Root.SetOpacity(1);
        if (drag.Ghost is { } ghost)
        {
            ghost.Release();
            drag.Layer.Content.Remove(ghost.Root);
        }
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

    /// <summary>Resolves the target under a point in rail coordinates and moves the ghost there.</summary>
    private void UpdateDrag(double railX, double railY)
    {
        if (_drag is not { } drag || drag.Ghost is not { } ghost) return;
        var target = Resolve(drag, railX, railY) ?? drag.Target;
        if (target is not { } resolved) return;
        drag.Target = resolved;
        var frame = resolved.Frame;
        ghost.SetFrame(new CalendarLayoutRect(frame.X, frame.Y, frame.Width, frame.Height));
        if (ghost.Model is { } model)
        {
            var time = drag.Resize ? resolved.End : resolved.Start;
            ghost.Model = model with { TimeRange = drag.Resize ? $"{TimeText(resolved.Start)} – {TimeText(time)}" : $"{TimeText(time)} – {TimeText(resolved.End)}" };
        }
        drag.Layer.Content.Move(ghost.Root, frame.X - drag.Layer.OriginX, frame.Y);
    }

    private WinoCalendarDragTarget? Resolve(DragSession drag, double railX, double railY)
    {
        var offset = RailOffset;
        switch (drag.Strip)
        {
            case CalendarStrip.Timed:
            {
                var width = _timedOverlay.GetWidth();
                railX = Math.Clamp(railX, HourColumnWidth + offset, Math.Max(HourColumnWidth, width - 1) + offset);
                return WinoCalendarSurfaceGeometry.ResolveTimedDrag(_windowDates, DayWidth, HourHeight, DragSnapMinutes, drag.Resize,
                    (drag.DownX, drag.DownY), (railX, railY), drag.OriginalStart, drag.OriginalEnd, drag.OriginFrame, drag.Tile.Date);
            }
            case CalendarStrip.AllDay:
            {
                var width = _allDayOverlay.GetWidth();
                railX = Math.Clamp(railX, HourColumnWidth + offset, Math.Max(HourColumnWidth, width - 1) + offset);
                return WinoCalendarSurfaceGeometry.ResolveAllDayDrag(_windowDates.Count, DayWidth, drag.DownX, railX, drag.OriginalStart, drag.OriginalEnd, drag.OriginFrame);
            }
            case CalendarStrip.Month:
            {
                // A bar covers several cells: it moves by the days between the pressed cell and the cell under the pointer.
                var pressed = MonthCellAt(drag.DownX, drag.DownY) is { } down ? DateOnly.FromDateTime(down) : drag.Tile.Date;
                if (MonthCellBounds(pressed) is not { } origin || MonthCellAt(railX, railY, clamp: true) is not { } day ||
                    MonthCellBounds(DateOnly.FromDateTime(day)) is not { } cell) return null;
                return WinoCalendarSurfaceGeometry.ResolveMonthDrag(pressed, Frame(origin), DateOnly.FromDateTime(day), Frame(cell),
                    drag.OriginalStart, drag.OriginalEnd, drag.OriginFrame);
            }
            default:
                return null;
        }

        static WinoLayoutFrame Frame(CalendarLayoutRect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);
    }

    /// <summary>
    /// Shows a drag of <paramref name="item"/> by <paramref name="days"/> and <paramref name="minutes"/> (a bottom-edge
    /// resize when <paramref name="resize"/> is true) without a pointer, through the same begin and move steps a pointer
    /// drag takes. Finish it with <see cref="FinishPreviewDrag"/>. False when the item has no tile on screen.
    /// </summary>
    public bool PreviewDrag(ICalendarItem item, int days, double minutes, bool resize)
    {
        EndTileDrag(commit: false);
        var strip = IsMonth ? CalendarStrip.Month : item.IsAllDayEvent || item.IsMultiDayEvent ? CalendarStrip.AllDay : CalendarStrip.Timed;
        if (LayerOf(strip) is not { } layer || FindVisible(layer, item) is not { } tile) return false;
        var frame = tile.Frame;
        var downX = frame.Width / 2;
        var downY = resize ? frame.Height - 2 : frame.Height / 2;
        if (!BeginTileDrag(strip, tile, downX, downY, resize)) return true;
        double dx, dy;
        if (strip == CalendarStrip.Month)
        {
            var width = _monthArea.GetWidth() / (double)CalendarSurfaceLayoutCalculator.MonthColumns;
            (dx, dy) = WinoCalendarSurfaceGeometry.MonthPreviewOffset(days, width, UnitSize);
        }
        else (dx, dy) = (days * DayWidth, strip == CalendarStrip.Timed ? minutes / 60 * HourHeight : 0);
        MoveTileDrag(tile, downX + dx, downY + dy);
        return true;
    }

    /// <summary>Ends a preview drag, raising the move or resize request when <paramref name="commit"/> is true.</summary>
    public void FinishPreviewDrag(bool commit) => EndTileDrag(commit);

#if DEBUG
    public string DescribeDrag()
        => _drag is not { } drag
            ? "drag=-"
            : string.Create(CultureInfo.InvariantCulture,
                $"drag='{drag.Item.Title}' strip={drag.Strip} resize={drag.Resize} from={drag.OriginalStart:yyyy-MM-dd HH:mm}..{drag.OriginalEnd:yyyy-MM-dd HH:mm} target={(drag.Target is { } t ? $"{t.Start:yyyy-MM-dd HH:mm}..{t.End:yyyy-MM-dd HH:mm}" : "-")} originOpacity={drag.Tile.Root.GetOpacity():0.##} ghost={drag.Ghost is not null}");
#endif
}
