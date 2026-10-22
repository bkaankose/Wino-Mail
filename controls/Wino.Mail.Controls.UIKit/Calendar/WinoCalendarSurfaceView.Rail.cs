using System.Collections;
using System.Collections.Specialized;
using Foundation;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Mail.Controls.UIKit.Calendar;

/// <summary>
/// A period on the rail (twin of the AppKit CalendarPeriodEventArgs): raised when the rail settles on one the view model
/// has not loaded, and while it moves, whenever the period nearest to the viewport changes.
/// </summary>
public sealed class CalendarPeriodEventArgs(CalendarDisplayType displayType, DateOnly anchorDate) : EventArgs
{
    public CalendarDisplayType DisplayType { get; } = displayType;

    /// <summary>The anchor to resolve with <see cref="CalendarRangeResolver"/>; the resolved range starts on the unit the rail settled on.</summary>
    public DateOnly AnchorDate { get; } = anchorDate;
}

/// <summary>
/// The rail (docs/calendar-rail-navigation.md; the Windows CalendarPeriodControl.Rail, the AppKit
/// WinoCalendarSurfaceView.Rail): the shared <see cref="CalendarRailSpace"/>, the fractional position, the realized window
/// of units around the viewport and the incremental reaction to item collection changes. The window covers
/// floor(position) - 1 … ceil(position) + VisibleUnitCount with spare units on both sides; it is re-centred only when the
/// viewport, plus one unit of margin, leaves it, so a frame of motion inside the window only moves the rails.
/// </summary>
public sealed partial class WinoCalendarSurfaceView
{
    /// <summary>Units realized before the first visible unit, and after the last one, when the window is re-centred.</summary>
    private const int SpareUnitCount = 3;

    /// <summary>Units that must stay realized beyond each side of the viewport before a shift is due.</summary>
    private const int WindowMarginUnits = 1;

    private readonly List<DateOnly> _windowDates = new();
    private CalendarRailSpace? _space;
    private double _position;
    private int _settledUnit;
    private int _rangeUnit;
    private int _windowStart;
    private int _windowCount;
    private double _unitSize;
    private bool _railDirty = true;
    private int _dominantMonthUnit = int.MinValue;
    private DateOnly _dominantMonth;

    /// <summary>The dates of the realized window: one per column, or seven per week row in the month view.</summary>
    internal IReadOnlyList<DateOnly> Dates => _windowDates;

    /// <summary>Columns or rows the viewport shows at once.</summary>
    internal int VisibleUnitCount => _space?.VisibleUnitCount ?? 1;

    internal bool IsMonth => _space?.Axis == CalendarRailAxis.Vertical;

    /// <summary>The width of a column, or the height of a week row, in points.</summary>
    internal double UnitSize => _unitSize;

    /// <summary>The month that owns most of the viewport; month cells outside it are dimmed.</summary>
    internal DateOnly DominantMonth => _dominantMonth;

    /// <summary>
    /// Builds the rail space for the published range and settings. A new display type or week layout rebuilds the rail on
    /// the period; otherwise the rail accepts the period (a glide when it is not the one it rests on).
    /// </summary>
    private void ApplyRange(bool fullRelayout)
    {
        if (_range is not { } range || _settings is not { } settings)
        {
            StopAllMotion();
            _space = null;
            _windowDates.Clear();
            _windowCount = 0;
            Invalidate();
            return;
        }

        var space = CalendarRailSpace.Create(range.DisplayType, settings);
        int targetUnit = space.GetPeriodStartUnit(range.AnchorDate);
        bool isRebuild = _space is null || !_space.HasSameUnits(space);
        _space = space;

        if (isRebuild)
        {
            StopAllMotion();
            _requestedUnits.Clear();
            _position = targetUnit;
            _settledUnit = targetUnit;
            _rangeUnit = targetUnit;
            _announcedViewportUnit = int.MinValue;
            _dominantMonthUnit = int.MinValue;
            Invalidate();
            return;
        }

        if (fullRelayout) Invalidate();
        AcceptVisibleRangeUnit(targetUnit);
    }

    private void SetUnitSize(double unitSize)
    {
        unitSize = double.IsFinite(unitSize) ? Math.Max(0, unitSize) : 0;
        if (Math.Abs(unitSize - _unitSize) < 0.0001) return;
        _unitSize = unitSize;
        _railDirty = true;
    }

    /// <summary>
    /// Brings the realized window in line with the position. Inside the window only the rails move; a shift (or
    /// <paramref name="force"/>) realizes the new window and lays it out.
    /// </summary>
    private void UpdateRailWindow(bool force)
    {
        if (_space is not { } space || _unitSize <= 0)
        {
            if (_windowCount > 0 || force)
            {
                _windowDates.Clear();
                _windowCount = 0;
                RelayoutRails();
            }
            _railDirty = false;
            return;
        }

        int firstVisibleUnit = (int)Math.Floor(_position);
        int lastVisibleUnit = (int)Math.Ceiling(_position) + space.VisibleUnitCount - 1;
        int windowCount = space.VisibleUnitCount + 1 + (SpareUnitCount * 2);
        bool isCovered = !force &&
                         _windowCount == windowCount &&
                         firstVisibleUnit - WindowMarginUnits >= _windowStart &&
                         lastVisibleUnit + WindowMarginUnits < _windowStart + _windowCount;

        if (!isCovered)
        {
            _windowStart = firstVisibleUnit - SpareUnitCount;
            _windowCount = windowCount;
            _windowDates.Clear();
            for (int unit = _windowStart; unit < _windowStart + _windowCount; unit++)
                _windowDates.AddRange(space.GetUnitDates(unit));
            _railDirty = false;
            RelayoutRails();
        }

        UpdateDominantMonth(force: !isCovered);
        ApplyRailOffset();
    }

    /// <summary>The month cells re-evaluate their owner whenever the viewport crosses a row boundary.</summary>
    private void UpdateDominantMonth(bool force)
    {
        if (_space is not { Axis: CalendarRailAxis.Vertical } space) return;
        int dominantUnit = (int)Math.Round(_position);
        if (!force && dominantUnit == _dominantMonthUnit) return;
        var month = space.GetDominantMonth(dominantUnit);
        _dominantMonthUnit = dominantUnit;
        if (month == _dominantMonth && !force) return;
        _dominantMonth = month;
        _monthRail.DominantMonthChanged();
    }

    /// <summary>
    /// The realized columns of the period that holds today (the current-time line spans it) and today's index among the
    /// realized dates; null when today is not realized.
    /// </summary>
    internal (int First, int Count, int TodayIndex)? TodaySpan()
    {
        if (_space is not { } space || IsMonth || _windowCount == 0) return null;
        var today = Today();
        int unit = space.GetUnitIndex(today);
        if (space.GetUnitStartDate(unit) != today) return null;
        int periodStart = space.SnapAtOrBefore(unit);
        int first = Math.Max(periodStart, _windowStart);
        int end = Math.Min(periodStart + space.VisibleUnitCount, _windowStart + _windowCount);
        if (end <= first) return null;
        int todayIndex = unit >= _windowStart && unit < _windowStart + _windowCount ? unit - _windowStart : -1;
        return (first - _windowStart, end - first, todayIndex);
    }

    private void ItemsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (NSThread.IsMain) HandleItemsChange(args);
        else BeginInvokeOnMainThread(() => HandleItemsChange(args));
    }

    /// <summary>
    /// Lays the window out again only when an added, removed or replaced item touches a realized unit (or is shown, for
    /// items whose dates changed in place); moves change nothing. The relayout runs once in the next layout pass however
    /// many notifications arrive.
    /// </summary>
    private void HandleItemsChange(NotifyCollectionChangedEventArgs args)
    {
        bool touches = args.Action switch
        {
            NotifyCollectionChangedAction.Move => false,
            NotifyCollectionChangedAction.Add => TouchesWindow(args.NewItems, includeShown: false),
            NotifyCollectionChangedAction.Remove => TouchesWindow(args.OldItems, includeShown: true),
            NotifyCollectionChangedAction.Replace => TouchesWindow(args.OldItems, includeShown: true) || TouchesWindow(args.NewItems, includeShown: false),
            _ => true
        };
        if (!touches) return;
        _railDirty = true;
        SetNeedsLayout();
    }

    private bool TouchesWindow(IList? items, bool includeShown)
    {
        if (items is null) return true;
        if (_windowDates.Count == 0) return false;
        var start = _windowDates[0].ToDateTime(TimeOnly.MinValue);
        var end = _windowDates[^1].AddDays(1).ToDateTime(TimeOnly.MinValue);
        foreach (var entry in items)
        {
            if (entry is not ICalendarItem item) continue;
            if (item.StartDate < end && (item.EndDate > start || item.StartDate >= start)) return true;
            if (includeShown && (_timedRail.Shows(item) || _allDayRail.Shows(item) || _monthRail.Shows(item))) return true;
        }
        return false;
    }

#if DEBUG
    /// <summary>The rail state, for the debug bridge.</summary>
    public string DescribeRail()
        => _space is not { } space
            ? "no rail"
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"type={space.DisplayType} axis={space.Axis} position={_position:0.###} settled={_settledUnit} range={_rangeUnit} " +
              $"anchor={space.GetAnchorDate(CalendarRailMotion.ChooseSnapUnit(space, _position, 0)):yyyy-MM-dd} window={_windowStart}+{_windowCount} " +
              $"dates={(_windowDates.Count > 0 ? $"{_windowDates[0]:yyyy-MM-dd}..{_windowDates[^1]:yyyy-MM-dd}" : "-")} unit={_unitSize:0.#} " +
              $"dominant={_dominantMonth:yyyy-MM} gliding={_isGliding} panning={_isPanning} requested=[{string.Join(",", _requestedUnits)}]");
#endif
}
