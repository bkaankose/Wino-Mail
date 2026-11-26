using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.Controls.Core.Calendar;

namespace Wino.Mail.Controls.Linux.Calendar;

/// <summary>The day header strip and the hour grid: drawing and the timed tile layout.</summary>
public sealed partial class WinoCalendarSurface
{
    private string[]? _hourLabels;
    private bool _gutterPending;

    private void DrawHeader(Cairo.Context cr, int width, int height)
    {
        var palette = Palette;
        palette.HeaderFill.Use(cr);
        cr.Rectangle(0, 0, width, height);
        cr.Fill();
        palette.Line.Use(cr);
        cr.Rectangle(0, height - 1, width, 1);
        cr.Fill();
        if (IsMonth)
        {
            DrawMonthHeader(cr, width, height);
            return;
        }
        cr.Rectangle(HourColumnWidth - 1, 0, 1, height);
        cr.Fill();
    }

    /// <summary>The day headers of the whole realized window in rail coordinates; drawn once per layout, moved by the rail.</summary>
    private void DrawHeaderColumns(Cairo.Context cr, int width, int height)
    {
        var dates = _windowDates;
        if (IsMonth || dates.Count == 0 || _headerTexts.Count != dates.Count) return;
        var palette = Palette;
        var dayWidth = DayWidth;
        var today = Today();
        cr.Save();
        cr.Translate(-HourColumnWidth, 0);
        for (var index = 0; index < dates.Count; index++)
        {
            var x = HourColumnWidth + index * dayWidth;
            palette.Line.Use(cr);
            cr.Rectangle(Math.Round(x) - 1, 0, 1, height);
            cr.Fill();
            var text = _layouts.Get(_headerTexts[index], CalendarTextStyle.DayHeader);
            var isToday = dates[index] == today;
            cr.Save();
            cr.Rectangle(x + 4, 0, Math.Max(0, dayWidth - 8), height);
            cr.Clip();
            if (isToday)
            {
                var pillWidth = Math.Min(Math.Max(0, dayWidth - 8), text.Width + 16);
                palette.Accent.Use(cr);
                WinoCalendarTile.RoundedRect(cr, x + (dayWidth - pillWidth) / 2, (height - 24) / 2.0, pillWidth, 24, 12);
                cr.Fill();
            }
            (isToday ? palette.AccentForeground : palette.Text).Use(cr);
            cr.MoveTo(x + Math.Max(4, (dayWidth - text.Width) / 2), (height - text.Height) / 2.0);
            PangoCairo.Functions.ShowLayout(cr, text.Layout);
            cr.Restore();
        }
        palette.Line.Use(cr);
        cr.Rectangle(Math.Round(HourColumnWidth + dates.Count * dayWidth) - 1, 0, 1, height);
        cr.Fill();
        cr.Restore();
    }

    /// <summary>The fixed hour gutter of the hour grid.</summary>
    private void DrawTimed(Cairo.Context cr, int width, int height)
    {
        if (_windowDates.Count == 0)
        {
            _gutterPending = true;
            return;
        }
        var palette = Palette;
        var hourHeight = HourHeight;
        var labels = _hourLabels ??= BuildHourLabels();
        palette.SecondaryText.Use(cr);
        for (var hour = 1; hour < 24; hour++)
        {
            var label = _layouts.Get(labels[hour], CalendarTextStyle.HourLabel);
            var y = TimelinePadding + hour * hourHeight;
            cr.MoveTo(HourColumnWidth - 8 - label.Width, y - label.Height / 2.0);
            PangoCairo.Functions.ShowLayout(cr, label.Layout);
        }
        palette.Line.Use(cr);
        cr.Rectangle(HourColumnWidth - 1, 0, 1, height);
        cr.Fill();
    }

    /// <summary>
    /// The hour grid's day columns are one drawing area per realized day, keyed by date and placed at its rail x in the
    /// moving content: a frame of motion moves them, and a window shift draws only the days that are new to the window.
    /// </summary>
    private sealed class TimedColumn(Gtk.DrawingArea area)
    {
        public Gtk.DrawingArea Area { get; } = area;
        public DateOnly Date;
        public int Index;
        public bool Used;
    }

    private readonly Dictionary<DateOnly, TimedColumn> _timedColumns = [];
    private readonly Stack<TimedColumn> _spareTimedColumns = new();
    private readonly List<DateOnly> _staleTimedColumns = [];
    private int _timedColumnWidth;
    private int _timedColumnHeight;

    private void SyncTimedColumns()
    {
        var dates = _windowDates;
        var dayWidth = DayWidth;
        var width = IsMonth ? 0 : Math.Max(1, (int)Math.Ceiling(dayWidth) + 2);
        var height = (int)WinoCalendarSurfaceGeometry.CanvasHeight(HourHeight);
        var resized = width != _timedColumnWidth || height != _timedColumnHeight;
        _timedColumnWidth = width;
        _timedColumnHeight = height;
        foreach (var column in _timedColumns.Values) column.Used = false;
        if (!IsMonth && dayWidth > 0)
        {
            for (var index = 0; index < dates.Count; index++)
            {
                var date = dates[index];
                if (!_timedColumns.TryGetValue(date, out var column))
                {
                    column = _spareTimedColumns.Count > 0 ? _spareTimedColumns.Pop() : CreateTimedColumn();
                    column.Date = date;
                    column.Area.SetVisible(true);
                    column.Area.QueueDraw();
                    _timedColumns[date] = column;
                }
                column.Used = true;
                column.Index = index;
                if (resized)
                {
                    column.Area.SetContentWidth(width);
                    column.Area.SetContentHeight(height);
                }
                _timedLayer.Content.Move(column.Area, Math.Round(index * dayWidth) - 1, 0);
            }
        }
        _staleTimedColumns.Clear();
        foreach (var (date, column) in _timedColumns) if (!column.Used) _staleTimedColumns.Add(date);
        foreach (var date in _staleTimedColumns)
        {
            if (!_timedColumns.Remove(date, out var column)) continue;
            column.Area.SetVisible(false);
            _spareTimedColumns.Push(column);
        }
    }

    private TimedColumn CreateTimedColumn()
    {
        var column = new TimedColumn(Gtk.DrawingArea.New());
        column.Area.SetCanTarget(false);
        column.Area.SetContentWidth(Math.Max(1, _timedColumnWidth));
        column.Area.SetContentHeight(Math.Max(1, _timedColumnHeight));
        var surface = _ref;
        column.Area.SetDrawFunc((_, cr, width, height) => surface.Value?.DrawTimedColumn(cr, column, height));
        // Below every tile: the content's first children are the rail drawings.
        _timedLayer.Content.Put(column.Area, 0, 0);
        column.Area.InsertAfter(_timedLayer.Content, null);
        return column;
    }

    private void QueueDrawTimedColumns()
    {
        foreach (var column in _timedColumns.Values) column.Area.QueueDraw();
    }

    /// <summary>One day column: shading, the 30-minute grid, its leading line, the current-time line of today's period.</summary>
    private void DrawTimedColumn(Cairo.Context cr, TimedColumn column, int height)
    {
        if (IsMonth || _space is null) return;
        var palette = Palette;
        var hourHeight = HourHeight;
        var dayWidth = DayWidth;
        var date = column.Date;
        var top = TimelinePadding;
        var settings = _settings;
        cr.Save();
        cr.Translate(1, 0);

        if (WinoCalendarSurfaceGeometry.IsShadedWeekend(date, VisibleUnitCount))
        {
            palette.WeekendFill.Use(cr);
            cr.Rectangle(0, 0, dayWidth, height);
            cr.Fill();
        }

        if (settings?.IsWorkingHoursEnabled == true)
        {
            var workStart = settings.WorkingHourStart.TotalHours;
            var workEnd = settings.WorkingHourEnd.TotalHours;
            palette.OffHours.Use(cr);
            if (!settings.WorkingDays.Contains(date.DayOfWeek)) cr.Rectangle(0, top, dayWidth, hourHeight * 24);
            else
            {
                cr.Rectangle(0, top, dayWidth, workStart * hourHeight);
                cr.Rectangle(0, top + workEnd * hourHeight, dayWidth, Math.Max(0, (24 - workEnd) * hourHeight));
            }
            cr.Fill();
        }

        var intervalHeight = hourHeight * WinoCalendarSurfaceGeometry.GridIntervalMinutes / 60;
        for (var pass = 0; pass < 2; pass++)
        {
            (pass == 0 ? palette.Line : palette.MinorLine).Use(cr);
            for (var interval = pass; interval <= WinoCalendarSurfaceGeometry.SlotsPerDay; interval += 2)
                cr.Rectangle(-1, Math.Round(top + interval * intervalHeight), dayWidth + 1, 1);
            cr.Fill();
        }

        palette.Line.Use(cr);
        cr.Rectangle(-1, 0, 1, height);
        cr.Fill();

        // Current time in today's column only, so it never reads as another day's.
        if (settings?.IsCurrentTimeLineEnabled != false && date == Today())
        {
            var y = top + DateTime.Now.TimeOfDay.TotalHours * hourHeight;
            palette.Now.Use(cr);
            cr.Rectangle(-1, y - 1, dayWidth + 1, 2);
            cr.Fill();
            cr.Arc(5, y, 5, 0, 2 * Math.PI);
            cr.Fill();
        }
        cr.Translate(-(HourColumnWidth + column.Index * dayWidth), 0);
        DrawSelection(cr, CalendarStrip.Timed);
        cr.Restore();
    }

    private string[] BuildHourLabels()
    {
        var labels = new string[24];
        for (var hour = 0; hour < 24; hour++) labels[hour] = _settings?.GetTimedHourLabelText(hour) ?? $"{hour:00}:00";
        return labels;
    }

    private void RelayoutTimed()
    {
        var placements = CalendarLayoutAdapter.CalculateTimed(_windowDates, _items, DayWidth, HourHeight, _settings?.EventDisplayMode ?? CalendarEventDisplayMode.Stacked);
        _timedLayer.Sync(placements, Tile, AccessibleNameFactory);
        SyncTimedColumns();
        // The gutter draws once the window has days; a shift alone never redraws it.
        if (_gutterPending)
        {
            _gutterPending = false;
            _timedArea.QueueDraw();
        }
    }

    /// <summary>Scrolls the hour grid so the event's tile is in view; false while the event has no tile yet.</summary>
    public bool RevealItem(ICalendarItem item)
    {
        if (_timedLayer.Find(item) is not { } tile) return FindTile(item) is not null;
        var adjustment = _scroller.GetVadjustment();
        var page = adjustment.GetPageSize();
        if (page <= 0) return false;
        var frame = tile.Frame;
        var value = adjustment.GetValue();
        if (frame.Y >= value && frame.Y + Math.Min(frame.Height, page) <= value + page) return true;
        adjustment.SetValue(Math.Max(0, frame.Y - HourHeight));
        return true;
    }

    /// <summary>The y of the current-time line on the hour grid, for the debug report.</summary>
    internal double NowLineY => TimelinePadding + DateTime.Now.TimeOfDay.TotalHours * HourHeight;
}
