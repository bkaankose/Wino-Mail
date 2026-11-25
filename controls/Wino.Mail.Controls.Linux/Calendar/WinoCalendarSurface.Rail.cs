using System.Collections;
using System.Collections.Specialized;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Presentation.Linux;

namespace Wino.Mail.Controls.Linux.Calendar;

/// <summary>
/// The rail (docs/calendar-rail-navigation.md, macOS WinoCalendarSurfaceView.Rail): the shared
/// <see cref="CalendarRailSpace"/>, the fractional position, the realized window of units around the viewport and the
/// incremental reaction to item changes. The window covers floor(position) - 1 … ceil(position) + VisibleUnitCount with
/// spare units on both sides and is re-centred only when the viewport, plus one unit of margin, leaves it.
/// </summary>
public sealed partial class WinoCalendarSurface
{
    private const int SpareUnitCount = 3;
    private const int WindowMarginUnits = 1;

    private readonly List<DateOnly> _windowDates = [];
    private readonly List<string> _headerTexts = [];
    private readonly HashSet<int> _requestedUnits = [];
    private CalendarRailSpace? _space;
    private double _position;
    private int _settledUnit;
    private int _rangeUnit;
    private int _latestRequestedUnit;
    private int _windowStart;
    private int _windowCount;
    private double _unitSize;
    private bool _railDirty = true;
    private int _dominantMonthUnit = int.MinValue;
    private int _spareUnits = SpareUnitCount;
    private DateOnly _dominantMonth;

    internal IReadOnlyList<DateOnly> Dates => _windowDates;

    internal int VisibleUnitCount => _space?.VisibleUnitCount ?? 1;

    internal bool IsMonth => _space?.Axis == CalendarRailAxis.Vertical;

    internal double UnitSize => _unitSize;

    internal double DayWidth => IsMonth ? 0 : _unitSize;

    internal DateOnly DominantMonth => _dominantMonth;

    internal CalendarRailSpace? Space => _space;

    /// <summary>The index of the shown period's first day in the realized window: all-day bars restart there.</summary>
    internal int RangeBreakIndex => _rangeUnit - _windowStart;

    /// <summary>How far the realized window is shifted along the rail, rounded to a device pixel.</summary>
    internal double RailOffset
    {
        get
        {
            var scale = Math.Max(1, _root.GetScaleFactor());
            return Math.Round((_position - _windowStart) * _unitSize * scale) / scale;
        }
    }

    internal (double X, double Y) ToRail(CalendarStrip strip, double x, double y)
        => strip == CalendarStrip.Month ? (x, y + RailOffset) : (x + RailOffset, y);

    internal (double X, double Y) ToStrip(CalendarStrip strip, double x, double y)
        => strip == CalendarStrip.Month ? (x, y - RailOffset) : (x - RailOffset, y);

    /// <summary>
    /// Builds the rail space for the published range and settings. A new display type or week layout rebuilds the rail on
    /// the period; otherwise the rail accepts the period.
    /// </summary>
    private void ApplyRange(bool fullRelayout)
    {
        if (_range is not { } range || _settings is not { } settings)
        {
            StopAllMotion();
            _announcedViewportUnit = int.MinValue;
            _space = null;
            _windowDates.Clear();
            _windowCount = 0;
            Invalidate();
            return;
        }

        var space = CalendarRailSpace.Create(range.DisplayType, settings);
        var targetUnit = space.GetPeriodStartUnit(range.AnchorDate);
        var isRebuild = _space is null || !_space.HasSameUnits(space);
        _space = space;
        _spareUnits = SpareUnitsFor(space, targetUnit);

        if (isRebuild)
        {
            StopAllMotion();
            _announcedViewportUnit = int.MinValue;
            _requestedUnits.Clear();
            _position = targetUnit;
            _settledUnit = targetUnit;
            _rangeUnit = targetUnit;
            _dominantMonthUnit = int.MinValue;
            Invalidate();
            return;
        }

        if (fullRelayout) Invalidate();
        AcceptVisibleRangeUnit(targetUnit);
    }

    /// <summary>
    /// The unit of a range the ViewModel published. A confirmation of the settled period moves nothing; a stale answer
    /// to an earlier request is ignored; any other unit (today, the date picker) moves the rail there.
    /// </summary>
    private void AcceptVisibleRangeUnit(int targetUnit)
    {
        var isStaleAnswer = _requestedUnits.Contains(targetUnit) && targetUnit != _latestRequestedUnit;
        if (targetUnit == _latestRequestedUnit) _requestedUnits.Clear();
        if (_rangeUnit != targetUnit)
        {
            _rangeUnit = targetUnit;
            RequestAllDayBreakRelayout();
        }
        if (isStaleAnswer || targetUnit == _settledUnit || (_isGliding && _glideTarget == targetUnit)) return;
        GlideToExternalUnit(targetUnit);
    }

    /// <summary>The bars split at the new period's first day on the next turn: never inside a frame tick, and only the all-day strip.</summary>
    private void RequestAllDayBreakRelayout()
    {
        if (IsMonth || _windowCount == 0) return;
        var surface = _ref;
        MainLoop.Post(() =>
        {
            if (surface.Value is not { } owner || owner._disposed || owner.IsMonth || owner._windowCount == 0) return;
            owner.RelayoutAllDay();
        });
    }

    private void SetRailPosition(double position)
    {
        _position = position;
        UpdateRailWindow(force: false);
        AnnounceViewportPeriod();
    }

    /// <summary>The rail rests on a unit; when that is not the published period (or a request is open) the page is asked to load it.</summary>
    private void SettleRail(int unit)
    {
        _settledUnit = unit;
        RecentreWindowAfterSettle();
        if (_space is not { } space || (unit == _rangeUnit && _requestedUnits.Count == 0)) return;
        _requestedUnits.Add(unit);
        _latestRequestedUnit = unit;
        PeriodSettled?.Invoke(this, new CalendarPeriodEventArgs(space.DisplayType, space.GetAnchorDate(unit)));
    }

    private void SetUnitSize(double unitSize)
    {
        unitSize = double.IsFinite(unitSize) ? Math.Max(0, unitSize) : 0;
        if (Math.Abs(unitSize - _unitSize) < 0.0001) return;
        _unitSize = unitSize;
        _railDirty = true;
    }

    /// <summary>Brings the realized window in line with the position; a shift (or <paramref name="force"/>) realizes and lays it out.</summary>
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

        var firstVisibleUnit = (int)Math.Floor(_position);
        var lastVisibleUnit = (int)Math.Ceiling(_position) + space.VisibleUnitCount - 1;
        var windowCount = space.VisibleUnitCount + 1 + (_spareUnits * 2);
        var isCovered = !force &&
                        _windowCount == windowCount &&
                        firstVisibleUnit - WindowMarginUnits >= _windowStart &&
                        lastVisibleUnit + WindowMarginUnits < _windowStart + _windowCount;

        if (!isCovered)
        {
#if DEBUG
            var shiftStarted = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            ShiftWindow(space, firstVisibleUnit - _spareUnits, windowCount);
            _railDirty = false;
            RelayoutRails();
#if DEBUG
            DebugRecordShift(System.Diagnostics.Stopwatch.GetElapsedTime(shiftStarted).TotalMilliseconds);
#endif
        }

        UpdateDominantMonth(force: !isCovered);
        ApplyRailOffset();
    }

    /// <summary>
    /// The hour grid keeps a whole period of spare days on each side, so a glide to the next or previous period stays
    /// inside the realized window and no frame of it realizes and rasterizes new day columns; the window is re-centred
    /// once the rail rests (<see cref="RecentreWindowAfterSettle"/>). The month grid keeps three spare weeks.
    /// </summary>
    private static int SpareUnitsFor(CalendarRailSpace space, int snapUnit)
        => space.Axis == CalendarRailAxis.Vertical ? SpareUnitCount : Math.Max(SpareUnitCount, Math.Abs(space.MovePeriod(snapUnit, 1) - snapUnit));

    /// <summary>On the turn after the rail came to rest, centres the realized window on it when it is off-centre.</summary>
    private void RecentreWindowAfterSettle()
    {
        var surface = _ref;
        MainLoop.Post(() =>
        {
            if (surface.Value is not { } owner || owner._disposed || owner._isGliding || owner._isPanning || owner._isWheelScrolling) return;
            if (owner._space is null || owner._unitSize <= 0 || owner._windowCount == 0) return;
            if (owner._windowStart == (int)Math.Floor(owner._position) - owner._spareUnits) return;
            owner.UpdateRailWindow(force: true);
        });
    }

    private void ShiftWindow(CalendarRailSpace space, int windowStart, int windowCount)
    {
        CloseOverflow();
#if DEBUG
        DebugShiftCount++;
#endif
        _windowStart = windowStart;
        _windowCount = windowCount;
        _windowDates.Clear();
        for (var unit = windowStart; unit < windowStart + windowCount; unit++)
            _windowDates.AddRange(space.GetUnitDates(unit));
    }

    private void RebuildHeaderTexts()
    {
        _headerTexts.Clear();
        var culture = _settings?.CultureInfo ?? System.Globalization.CultureInfo.CurrentCulture;
        foreach (var date in _windowDates)
            _headerTexts.Add(_settings?.GetTimedDayHeaderText(date) ?? date.ToDateTime(TimeOnly.MinValue).ToString("ddd dd", culture));
    }

    private void UpdateDominantMonth(bool force)
    {
        if (_space is not { Axis: CalendarRailAxis.Vertical } space) return;
        var dominantUnit = (int)Math.Round(_position);
        if (!force && dominantUnit == _dominantMonthUnit) return;
        var month = space.GetDominantMonth(dominantUnit);
        _dominantMonthUnit = dominantUnit;
        if (month == _dominantMonth && !force) return;
        _dominantMonth = month;
        _monthGrid.QueueDraw();
    }

    /// <summary>The realized columns of the period that holds today (the current-time line spans it) and today's index; null when today is not realized.</summary>
    internal (int First, int Count, int TodayIndex)? TodaySpan()
    {
        if (_space is not { } space || IsMonth || _windowCount == 0) return null;
        var today = Today();
        var unit = space.GetUnitIndex(today);
        if (space.GetUnitStartDate(unit) != today) return null;
        var periodStart = space.SnapAtOrBefore(unit);
        var first = Math.Max(periodStart, _windowStart);
        var end = Math.Min(periodStart + space.VisibleUnitCount, _windowStart + _windowCount);
        if (end <= first) return null;
        var todayIndex = unit >= _windowStart && unit < _windowStart + _windowCount ? unit - _windowStart : -1;
        return (first - _windowStart, end - first, todayIndex);
    }

    /// <summary>Lays the window out again only when a change touches a realized unit (or a shown item); moves change nothing.</summary>
    private void HandleItemsChange(NotifyCollectionChangedEventArgs args)
    {
        if (_disposed) return;
        var touches = args.Action switch
        {
            NotifyCollectionChangedAction.Move => false,
            NotifyCollectionChangedAction.Add => TouchesWindow(args.NewItems, includeShown: false),
            NotifyCollectionChangedAction.Remove => TouchesWindow(args.OldItems, includeShown: true),
            NotifyCollectionChangedAction.Replace => TouchesWindow(args.OldItems, includeShown: true) || TouchesWindow(args.NewItems, includeShown: false),
            _ => true
        };
        if (!touches) return;
        _railDirty = true;
        RequestRelayout();
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
            if (includeShown && (_timedLayer.Shows(item) || _allDayLayer.Shows(item) || _monthLayer.Shows(item))) return true;
        }
        return false;
    }

#if DEBUG
    public string DescribeRail()
        => _space is not { } space
            ? "no rail"
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"type={space.DisplayType} axis={space.Axis} position={_position:0.###} settled={_settledUnit} range={_rangeUnit} " +
              $"anchor={space.GetAnchorDate(_settledUnit):yyyy-MM-dd} window={_windowStart}+{_windowCount} " +
              $"dates={(_windowDates.Count > 0 ? $"{_windowDates[0]:yyyy-MM-dd}..{_windowDates[^1]:yyyy-MM-dd}" : "-")} unit={_unitSize:0.#} offset={RailOffset:0.#} " +
              $"gliding={_isGliding} panning={_isPanning} wheel={_isWheelScrolling} requested=[{string.Join(",", _requestedUnits)}] relayout={_relayoutRequested}/{_relayoutTick} layouts={_layoutCount} mapped={_root.GetMapped()}");
#endif
}
