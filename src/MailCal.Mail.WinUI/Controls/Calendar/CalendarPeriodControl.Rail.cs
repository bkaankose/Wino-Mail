using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Numerics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using MailCal.Calendar.ViewModels.Data;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Calendar;
using WinRT;

namespace MailCal.Calendar.Controls;

/// <summary>
/// The rail: the realized window of units around the viewport, the recyclable unit containers,
/// the item presenter pool and the incremental reaction to item collection changes.
/// </summary>
public sealed partial class CalendarPeriodControl
{
    /// <summary>
    /// Units realized before the first visible unit, and after the last one, when the window is
    /// re-centred. The window shifts only once the viewport gets within <see cref="WindowMarginUnits"/>
    /// of its edge, so a rail that jitters across one boundary never re-realizes anything.
    /// </summary>
    private const int SpareUnitCount = 3;

    /// <summary>
    /// Units that must stay realized beyond each side of the viewport before a shift is due.
    /// </summary>
    private const int WindowMarginUnits = 1;

    /// <summary>
    /// How far the rail may travel from its origin unit before unit coordinates are rebased.
    /// </summary>
    private const int RebaseDistanceUnits = 2048;

    private const int AllDayPresenterSlot = -1;
    private const int TimedBodyPresenterSlot = -2;
    private const double MonthDayLabelInsetX = 4d;
    private const double MonthDayLabelInsetY = 2d;
    private const double DimmedMonthDayLabelOpacity = 0.55d;

    /// <summary>
    /// Column lines in the header and all-day strips are drawn just left of each unit boundary, so
    /// the boundary that rests on the viewport's left edge is clipped instead of doubling the hour
    /// column's border.
    /// </summary>
    private const float BoundaryLineInset = 0.5f;

    private readonly Dictionary<int, RailUnit> _units = new();
    private readonly Stack<TimedRailUnit> _timedUnitPool = new();
    private readonly Stack<MonthRailUnit> _monthUnitPool = new();
    private readonly Dictionary<DataTemplate, Stack<ContentPresenter>> _presenterPool = new();
    private readonly List<DateOnly> _windowDates = new();
    private readonly HashSet<int> _dirtyUnits = new();
    private readonly List<int> _unitScratch = new();

    private CalendarRailSpace? _space;
    private double _position;
    private int _settledUnit;
    private int _rangeUnit;
    private int _windowStart;
    private int _windowCount;

    // Units and canvas hosts are placed relative to this unit, not to the window start, so a window
    // shift places only the units it realizes. The rail translation is measured from it too.
    private int _originUnit;
    private double _unitSize;
    private double _monthCellWidth;
    private double _monthRowWidth;
    private bool _isDirtyLayoutQueued;
    private int _dominantMonthUnit = int.MinValue;
    private DateOnly _dominantMonth;
    private DataTemplate? _allDayTemplate;
    private DataTemplate? _timedTemplate;
    private DataTemplate? _monthTemplate;

    // The window start each structure canvas was last drawn for. A Win2D canvas redraws on a later
    // frame than the window shift, so until it does, its host is offset to keep the old drawing
    // aligned with the units. The draw handler resets the offset in the frame it draws.
    private int _timedHeaderCanvasStart;
    private int _timedAllDayCanvasStart;
    private int _timedStructureCanvasStart;
    private int _monthStructureCanvasStart;

    private bool IsMonthRail => _space?.Axis == CalendarRailAxis.Vertical;

    /// <summary>
    /// How far the realized window's first unit sits before the viewport, in pixels along the rail axis.
    /// </summary>
    private double GetRailOffset() => (_position - _windowStart) * _unitSize;

    /// <summary>
    /// The rail translation: how far the origin unit sits before the viewport, in pixels.
    /// </summary>
    private double GetRailTranslation() => -(_position - _originUnit) * _unitSize;

    /// <summary>
    /// Brings the realized window in line with the rail position. The window is re-centred only
    /// when the viewport, plus one unit of margin, is no longer inside it; otherwise only the rail
    /// translation changes, which needs no XAML layout.
    /// </summary>
    private void UpdateRailWindow(bool forceRelayout)
    {
        if (_space is not CalendarRailSpace space || _unitSize <= 0d || _areCanvasesReleased)
        {
            return;
        }

        var firstVisibleUnit = (int)Math.Floor(_position);
        var lastVisibleUnit = (int)Math.Ceiling(_position) + space.VisibleUnitCount - 1;
        var windowCount = space.VisibleUnitCount + 1 + (SpareUnitCount * 2);
        var isCovered = !forceRelayout &&
                        _units.Count > 0 &&
                        windowCount == _windowCount &&
                        firstVisibleUnit - WindowMarginUnits >= _windowStart &&
                        lastVisibleUnit + WindowMarginUnits < _windowStart + _windowCount;

        if (!isCovered)
        {
            ShiftWindow(space, firstVisibleUnit - SpareUnitCount, windowCount);
        }

        UpdateDominantMonth(force: !isCovered);
        ApplyRailTranslation();
    }

    private void ShiftWindow(CalendarRailSpace space, int windowStart, int windowCount)
    {
        var isRebased = false;

        if (_units.Count == 0 || Math.Abs(windowStart - _originUnit) > RebaseDistanceUnits)
        {
            _originUnit = windowStart;
            isRebased = true;
        }

        _unitScratch.Clear();

        foreach (var unitIndex in _units.Keys)
        {
            if (unitIndex < windowStart || unitIndex >= windowStart + windowCount)
            {
                _unitScratch.Add(unitIndex);
            }
        }

        foreach (var unitIndex in _unitScratch)
        {
            if (_units.Remove(unitIndex, out var leavingUnit))
            {
                RecycleUnit(leavingUnit);
            }
        }

        _windowStart = windowStart;
        _windowCount = windowCount;
        _windowDates.Clear();

        for (var unitIndex = windowStart; unitIndex < windowStart + windowCount; unitIndex++)
        {
            _windowDates.AddRange(space.GetUnitDates(unitIndex));

            if (!_units.ContainsKey(unitIndex))
            {
                var unit = RealizeUnit(space, unitIndex);

                _units[unitIndex] = unit;
                PlaceUnit(unit);
                LayoutUnit(unit);
            }
        }

        if (isRebased)
        {
            PlaceUnits();
        }

        PlaceStructureCanvases();
        SizeStructureCanvases();
        UpdateAllDayHeight();
        InvalidateAllCanvases();
    }

    /// <summary>
    /// Puts each structure canvas host at the window start, measured from the origin unit.
    /// </summary>
    private void PlaceStructureCanvases()
    {
        var offset = (_windowStart - _originUnit) * _unitSize;

        if (IsMonthRail)
        {
            Canvas.SetTop(MonthStructureCanvasHost, offset);
            return;
        }

        Canvas.SetLeft(TimedHeaderCanvasHost, offset);
        Canvas.SetLeft(TimedAllDayCanvasHost, offset);
        Canvas.SetLeft(TimedStructureCanvasHost, offset);
    }

    private void ApplyRailTranslation()
    {
        var offset = (float)GetRailTranslation();

        if (IsMonthRail)
        {
            MonthRail.Translation = new Vector3(0f, offset, 0f);
            MonthStructureCanvasHost.Translation = new Vector3(0f, GetCanvasLag(_monthStructureCanvasStart), 0f);
            return;
        }

        var translation = new Vector3(offset, 0f, 0f);

        TimedHeaderRail.Translation = translation;
        TimedAllDayRail.Translation = translation;
        TimedBodyRail.Translation = translation;

        TimedHeaderCanvasHost.Translation = new Vector3(GetCanvasLag(_timedHeaderCanvasStart), 0f, 0f);
        TimedAllDayCanvasHost.Translation = new Vector3(GetCanvasLag(_timedAllDayCanvasStart), 0f, 0f);
        TimedStructureCanvasHost.Translation = new Vector3(GetCanvasLag(_timedStructureCanvasStart), 0f, 0f);
    }

    /// <summary>
    /// How far a canvas drawn for an earlier window start must sit from the current window start
    /// so that its drawing stays on the units it describes.
    /// </summary>
    private float GetCanvasLag(int drawnWindowStart) => (float)((drawnWindowStart - _windowStart) * _unitSize);

    /// <summary>
    /// Called by each draw handler: the canvas now shows the current window, so its host returns to
    /// the window start in the same frame the new drawing appears.
    /// </summary>
    private void MarkCanvasDrawn(ref int drawnWindowStart, UIElement canvasHost)
    {
        drawnWindowStart = _windowStart;
        canvasHost.Translation = Vector3.Zero;
    }

    private void RailHostSizeChanged(object sender, SizeChangedEventArgs e) => UpdateRailClips();

    /// <summary>
    /// Keeps each moving rail inside its host so that it never paints over the hour column or the
    /// month weekday header.
    /// </summary>
    private void UpdateRailClips()
    {
        TimedHeaderClip.Rect = new Rect(0d, 0d, TimedHeaderHost.ActualWidth, TimedHeaderHost.ActualHeight);
        TimedAllDayClip.Rect = new Rect(0d, 0d, TimedAllDayHost.ActualWidth, TimedAllDayHost.ActualHeight);
        TimedViewportClip.Rect = new Rect(0d, 0d, TimedViewport.ActualWidth, TimedViewport.ActualHeight);
        MonthViewportClip.Rect = new Rect(0d, 0d, MonthViewport.ActualWidth, MonthViewport.ActualHeight);
    }

    private void PlaceUnits()
    {
        foreach (var unit in _units.Values)
        {
            PlaceUnit(unit);
        }
    }

    /// <summary>
    /// Positions a unit's containers along the rail, measured from the origin unit.
    /// </summary>
    private void PlaceUnit(RailUnit unit)
    {
        var offset = (unit.Index - _originUnit) * _unitSize;

        if (unit is TimedRailUnit timedUnit)
        {
            Canvas.SetLeft(timedUnit.Header, offset);
            timedUnit.Header.Width = _unitSize;
            timedUnit.Header.Height = TimedHeaderHeight;

            Canvas.SetLeft(timedUnit.AllDayCanvas, offset);
            timedUnit.AllDayCanvas.Width = _unitSize;

            Canvas.SetLeft(timedUnit.BodyCanvas, offset);
            timedUnit.BodyCanvas.Width = _unitSize;
            timedUnit.BodyCanvas.Height = TimelineHeight;
        }
        else if (unit is MonthRailUnit monthUnit)
        {
            Canvas.SetTop(monthUnit.Row, offset);
            monthUnit.Row.Width = _monthRowWidth;
            monthUnit.Row.Height = _unitSize;

            for (var column = 0; column < monthUnit.DayLabels.Length; column++)
            {
                var label = monthUnit.DayLabels[column];

                Canvas.SetLeft(label, (column * _monthCellWidth) + MonthDayLabelInsetX);
                Canvas.SetTop(label, MonthDayLabelInsetY);
                label.Width = Math.Max(0d, _monthCellWidth - MonthDayLabelInsetX);
            }
        }
    }

    /// <summary>
    /// Sizes each structure canvas to the realized window. It sits at the window start inside its
    /// rail, so the rail translation moves it with the units.
    /// </summary>
    private void SizeStructureCanvases()
    {
        var span = _windowCount * _unitSize;

        if (IsMonthRail)
        {
            MonthStructureCanvasHost.Width = _monthRowWidth;
            MonthStructureCanvasHost.Height = span;
            return;
        }

        TimedHeaderCanvasHost.Width = span;
        TimedHeaderCanvasHost.Height = TimedHeaderHeight;
        TimedAllDayCanvasHost.Width = span;
        TimedAllDayCanvasHost.Height = TimedAllDayHeight;
        TimedStructureCanvasHost.Width = span;
        TimedStructureCanvasHost.Height = TimelineHeight;
    }

    private void UpdateAllDayHeight()
    {
        if (IsMonthRail)
        {
            return;
        }

        var laneCount = 0;

        foreach (var unit in _units.Values)
        {
            if (unit is TimedRailUnit timedUnit)
            {
                laneCount = Math.Max(laneCount, timedUnit.AllDayLaneCount);
            }
        }

        var allDayHeight = TimedCalendarLayoutCalculator.GetAllDayHeight(laneCount);

        if (allDayHeight == TimedAllDayHeight)
        {
            return;
        }

        TimedAllDayHeight = allDayHeight;
        TimedAllDayCanvasHost.Height = allDayHeight;

        if (!_areCanvasesReleased)
        {
            TimedAllDayCanvas.Invalidate();
        }
    }

    /// <summary>
    /// Month cells outside the month that owns most of the viewport render dimmed. The owner is
    /// re-evaluated whenever the viewport crosses a row boundary.
    /// </summary>
    private void UpdateDominantMonth(bool force)
    {
        if (_space is not CalendarRailSpace space || space.Axis != CalendarRailAxis.Vertical)
        {
            return;
        }

        var dominantUnit = (int)Math.Round(_position);

        if (!force && dominantUnit == _dominantMonthUnit)
        {
            return;
        }

        _dominantMonthUnit = dominantUnit;
        _dominantMonth = space.GetDominantMonth(dominantUnit);

        foreach (var unit in _units.Values)
        {
            if (unit is MonthRailUnit monthUnit)
            {
                ApplyMonthLabelOpacity(monthUnit);
            }
        }
    }

    private void ApplyMonthLabelOpacity(MonthRailUnit unit)
    {
        for (var column = 0; column < unit.DayLabels.Length && column < unit.Dates.Count; column++)
        {
            var date = unit.Dates[column];
            var isDominantMonth = date.Year == _dominantMonth.Year && date.Month == _dominantMonth.Month;

            unit.DayLabels[column].Opacity = isDominantMonth ? 1d : DimmedMonthDayLabelOpacity;
        }
    }

    private RailUnit RealizeUnit(CalendarRailSpace space, int unitIndex)
    {
        var dates = space.GetUnitDates(unitIndex);
        var rangeStart = dates[0].ToDateTime(TimeOnly.MinValue);
        var rangeEnd = dates[^1].AddDays(1).ToDateTime(TimeOnly.MinValue);

        if (space.Axis == CalendarRailAxis.Vertical)
        {
            var monthUnit = _monthUnitPool.Count > 0 ? _monthUnitPool.Pop() : CreateMonthUnit();

            monthUnit.Assign(unitIndex, dates, rangeStart, rangeEnd);
            monthUnit.Row.Visibility = Visibility.Visible;

            for (var column = 0; column < monthUnit.DayLabels.Length && column < dates.Count; column++)
            {
                monthUnit.DayLabels[column].Text = dates[column].Day.ToString(CalendarSettings?.CultureInfo);
            }

            ApplyMonthLabelOpacity(monthUnit);

            return monthUnit;
        }

        var timedUnit = _timedUnitPool.Count > 0 ? _timedUnitPool.Pop() : CreateTimedUnit();

        timedUnit.Assign(unitIndex, dates, rangeStart, rangeEnd);
        timedUnit.HeaderText.Text = GetTimedHeaderText(dates[0]);
        timedUnit.Header.Visibility = Visibility.Visible;
        timedUnit.AllDayCanvas.Visibility = Visibility.Visible;
        timedUnit.BodyCanvas.Visibility = Visibility.Visible;

        return timedUnit;
    }

    private TimedRailUnit CreateTimedUnit()
    {
        var headerText = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false
        };

        var header = new Grid { IsHitTestVisible = false };
        header.Children.Add(headerText);

        var allDayCanvas = new Canvas();
        var bodyCanvas = new Canvas();

        TimedHeaderRail.Children.Add(header);
        TimedAllDayRail.Children.Add(allDayCanvas);
        TimedBodyRail.Children.Add(bodyCanvas);

        return new TimedRailUnit(header, headerText, allDayCanvas, bodyCanvas);
    }

    private MonthRailUnit CreateMonthUnit()
    {
        var row = new Canvas();
        var labels = new TextBlock[MonthCalendarLayoutCalculator.ColumnCount];

        for (var column = 0; column < labels.Length; column++)
        {
            var label = new TextBlock { IsHitTestVisible = false };

            labels[column] = label;
            row.Children.Add(label);
        }

        MonthRail.Children.Add(row);

        return new MonthRailUnit(row, labels);
    }

    /// <summary>
    /// Returns a unit's presenters to the pool and parks its containers, collapsed, for reuse.
    /// </summary>
    private void RecycleUnit(RailUnit unit)
    {
        foreach (var entry in unit.Presenters.Values)
        {
            ReleasePresenter(entry);
        }

        unit.Presenters.Clear();
        unit.Items.Clear();
        _dirtyUnits.Remove(unit.Index);

        if (unit is TimedRailUnit timedUnit)
        {
            timedUnit.AllDayLaneCount = 0;
            timedUnit.Header.Visibility = Visibility.Collapsed;
            timedUnit.AllDayCanvas.Visibility = Visibility.Collapsed;
            timedUnit.BodyCanvas.Visibility = Visibility.Collapsed;
            _timedUnitPool.Push(timedUnit);
        }
        else if (unit is MonthRailUnit monthUnit)
        {
            monthUnit.Row.Visibility = Visibility.Collapsed;
            _monthUnitPool.Push(monthUnit);
        }
    }

    private void RecycleAllUnits()
    {
        foreach (var unit in _units.Values)
        {
            RecycleUnit(unit);
        }

        _units.Clear();
        _dirtyUnits.Clear();
        _windowDates.Clear();
        _windowCount = 0;
    }

    /// <summary>
    /// Drops every unit, presenter and item reference when the control leaves the tree.
    /// </summary>
    private void ReleaseRail()
    {
        RecycleAllUnits();

        _presenterPool.Clear();
        _timedUnitPool.Clear();
        _monthUnitPool.Clear();
        _space = null;
    }

    [DynamicWindowsRuntimeCast(typeof(DataTemplate))]
    private void ResolveItemTemplates(CalendarEventDisplayMode displayMode)
    {
        var eventTemplate = (DataTemplate)Resources["CalendarEventTemplate"];

        _allDayTemplate = eventTemplate;
        _timedTemplate = displayMode switch
        {
            CalendarEventDisplayMode.ProtectTitles => (DataTemplate)Resources["ProtectedTitleCalendarEventTemplate"],
            CalendarEventDisplayMode.Overlapped or CalendarEventDisplayMode.LimitedOverlap => (DataTemplate)Resources["OverlappedCalendarEventTemplate"],
            _ => eventTemplate
        };
        _monthTemplate = (DataTemplate)Resources["MonthEventTemplate"];
    }

    /// <summary>
    /// Lays out one unit on its own. Presenters for items that stay are moved, new ones come from
    /// the pool and the ones for items that left go back to it.
    /// </summary>
    private void LayoutUnit(RailUnit unit)
    {
        var items = CollectUnitItems(unit);

        unit.BeginReconcile();

        if (unit is TimedRailUnit timedUnit)
        {
            LayoutTimedUnit(timedUnit, items);
        }
        else if (unit is MonthRailUnit monthUnit)
        {
            LayoutMonthUnit(monthUnit, items);
        }

        foreach (var staleEntry in unit.EndReconcile())
        {
            ReleasePresenter(staleEntry);
        }
    }

    private void LayoutTimedUnit(TimedRailUnit unit, List<CalendarItemViewModel> items)
    {
        var date = unit.Dates[0];
        var displayMode = CalendarSettings?.EventDisplayMode ?? CalendarEventDisplayMode.Stacked;
        var allDayLayouts = TimedCalendarLayoutCalculator.CalculateAllDayItems(date, items, _unitSize);
        var bodyLayouts = TimedCalendarLayoutCalculator.CalculateDay(date, items, _unitSize, GetHourHeight(), displayMode);

        unit.AllDayLaneCount = allDayLayouts.Count;

        if (_allDayTemplate is DataTemplate allDayTemplate)
        {
            foreach (var layout in allDayLayouts)
            {
                PlacePresenter(unit, new PresenterKey(layout.Item, AllDayPresenterSlot), unit.AllDayCanvas, allDayTemplate, layout.Bounds);
                PrepareDisplayMetadata(layout.Item, layout.Date);
            }
        }

        if (_timedTemplate is DataTemplate timedTemplate)
        {
            foreach (var layout in bodyLayouts)
            {
                var bounds = layout.Bounds with { Width = Math.Max(0d, layout.Bounds.Width - TimedItemRightSpacing) };

                PlacePresenter(unit, new PresenterKey(layout.Item, TimedBodyPresenterSlot), unit.BodyCanvas, timedTemplate, bounds);
                PrepareDisplayMetadata(layout.Item, layout.Date);
            }
        }
    }

    private void LayoutMonthUnit(MonthRailUnit unit, List<CalendarItemViewModel> items)
    {
        if (_monthTemplate is not DataTemplate monthTemplate)
        {
            return;
        }

        var layouts = MonthCalendarLayoutCalculator.CalculateRow(unit.Dates, items, _monthCellWidth, _unitSize);

        foreach (var layout in layouts)
        {
            PlacePresenter(unit, new PresenterKey(layout.Item, layout.Column), unit.Row, monthTemplate, layout.Bounds);
            PrepareDisplayMetadata(layout.Item, layout.Date);
        }
    }

    private void PlacePresenter(RailUnit unit, PresenterKey key, Canvas host, DataTemplate template, LayoutRect bounds)
    {
        var entry = unit.TakeExisting(key);

        if (entry is not null && (!ReferenceEquals(entry.Host, host) || !ReferenceEquals(entry.Template, template)))
        {
            ReleasePresenter(entry);
            entry = null;
        }

        if (entry is null)
        {
            entry = new PresenterEntry(AcquirePresenter(template), host, template);
            host.Children.Add(entry.Presenter);
        }

        var presenter = entry.Presenter;

        if (!ReferenceEquals(presenter.Content, key.Item))
        {
            presenter.Content = key.Item;
        }

        presenter.Width = Math.Max(0d, bounds.Width);
        presenter.Height = Math.Max(0d, bounds.Height);
        Canvas.SetLeft(presenter, bounds.X);
        Canvas.SetTop(presenter, bounds.Y);

        unit.Keep(key, entry);
    }

    private ContentPresenter AcquirePresenter(DataTemplate template)
    {
        if (_presenterPool.TryGetValue(template, out var pool) && pool.Count > 0)
        {
            return pool.Pop();
        }

        return new ContentPresenter { ContentTemplate = template };
    }

    private void ReleasePresenter(PresenterEntry entry)
    {
        entry.Host.Children.Remove(entry.Presenter);
        entry.Presenter.Content = null;

        // Presenters of a template that is no longer in use (the event display mode changed) are dropped.
        if (!ReferenceEquals(entry.Template, _allDayTemplate) &&
            !ReferenceEquals(entry.Template, _timedTemplate) &&
            !ReferenceEquals(entry.Template, _monthTemplate))
        {
            return;
        }

        if (!_presenterPool.TryGetValue(entry.Template, out var pool))
        {
            pool = new Stack<ContentPresenter>();
            _presenterPool[entry.Template] = pool;
        }

        pool.Push(entry.Presenter);
    }

    private List<CalendarItemViewModel> CollectUnitItems(RailUnit unit)
    {
        var items = new List<CalendarItemViewModel>();

        foreach (var item in CurrentItems)
        {
            if (Intersects(item, unit))
            {
                items.Add(item);
            }
        }

        return items;
    }

    private static bool Intersects(CalendarItemViewModel item, RailUnit unit)
    {
        if (!CalendarItemAccessor.TryGetTimeRange(item, out var start, out var end))
        {
            return false;
        }

        return start.LocalDateTime < unit.RangeEnd && end.LocalDateTime > unit.RangeStart;
    }

    private void ItemsSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            HandleItemsSourceChange(e);
            return;
        }

        DispatcherQueue.TryEnqueue(() => HandleItemsSourceChange(e));
    }

    /// <summary>
    /// Marks only the realized units an added, removed or replaced item touches; the order of the
    /// items does not affect layout, so moves are ignored.
    /// </summary>
    private void HandleItemsSourceChange(NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Move:
                return;
            case NotifyCollectionChangedAction.Add:
                MarkItemsDirty(e.NewItems, includeShownUnits: false);
                break;
            case NotifyCollectionChangedAction.Remove:
                MarkItemsDirty(e.OldItems, includeShownUnits: true);
                break;
            case NotifyCollectionChangedAction.Replace:
                MarkItemsDirty(e.OldItems, includeShownUnits: true);
                MarkItemsDirty(e.NewItems, includeShownUnits: false);
                break;
            default:
                MarkAllUnitsDirty();
                break;
        }

        QueueDirtyUnitLayout();
    }

    private void MarkItemsDirty(IList? changedItems, bool includeShownUnits)
    {
        if (changedItems is null)
        {
            MarkAllUnitsDirty();
            return;
        }

        foreach (var changedItem in changedItems)
        {
            if (changedItem is not CalendarItemViewModel item)
            {
                continue;
            }

            foreach (var unit in _units.Values)
            {
                if (Intersects(item, unit) || (includeShownUnits && unit.Items.Contains(item)))
                {
                    _dirtyUnits.Add(unit.Index);
                }
            }
        }
    }

    private void MarkAllUnitsDirty()
    {
        foreach (var unitIndex in _units.Keys)
        {
            _dirtyUnits.Add(unitIndex);
        }
    }

    private void QueueDirtyUnitLayout()
    {
        if (_isDirtyLayoutQueued || _dirtyUnits.Count == 0)
        {
            return;
        }

        _isDirtyLayoutQueued = true;
        DispatcherQueue.TryEnqueue(ApplyDirtyUnits);
    }

    private void ApplyDirtyUnits()
    {
        _isDirtyLayoutQueued = false;

        if (_areCanvasesReleased || _space is null || _unitSize <= 0d)
        {
            _dirtyUnits.Clear();
            return;
        }

        foreach (var unitIndex in _dirtyUnits)
        {
            if (_units.TryGetValue(unitIndex, out var unit))
            {
                LayoutUnit(unit);
            }
        }

        _dirtyUnits.Clear();
        UpdateAllDayHeight();
    }

    private readonly record struct PresenterKey(CalendarItemViewModel Item, int Slot);

    private sealed record PresenterEntry(ContentPresenter Presenter, Canvas Host, DataTemplate Template);

    /// <summary>
    /// One realized column or row of the rail. Containers are kept across reuse; only the unit
    /// index, its dates and its presenters change.
    /// </summary>
    private abstract class RailUnit
    {
        private Dictionary<PresenterKey, PresenterEntry> _reconcilePrevious = new();

        public int Index { get; private set; }

        public IReadOnlyList<DateOnly> Dates { get; private set; } = Array.Empty<DateOnly>();

        public DateTime RangeStart { get; private set; }

        public DateTime RangeEnd { get; private set; }

        public Dictionary<PresenterKey, PresenterEntry> Presenters { get; private set; } = new();

        /// <summary>
        /// The items this unit currently shows, so that a removed item clears its presenters even
        /// when its dates changed in place.
        /// </summary>
        public HashSet<CalendarItemViewModel> Items { get; } = new();

        public void Assign(int index, IReadOnlyList<DateOnly> dates, DateTime rangeStart, DateTime rangeEnd)
        {
            Index = index;
            Dates = dates;
            RangeStart = rangeStart;
            RangeEnd = rangeEnd;
        }

        public void BeginReconcile()
        {
            (_reconcilePrevious, Presenters) = (Presenters, _reconcilePrevious);
            Presenters.Clear();
            Items.Clear();
        }

        public PresenterEntry? TakeExisting(PresenterKey key)
            => _reconcilePrevious.Remove(key, out var entry) ? entry : null;

        public void Keep(PresenterKey key, PresenterEntry entry)
        {
            Presenters[key] = entry;
            Items.Add(key.Item);
        }

        /// <summary>
        /// Ends a layout pass and returns the presenters the pass did not keep.
        /// </summary>
        public List<PresenterEntry> EndReconcile()
        {
            var staleEntries = new List<PresenterEntry>(_reconcilePrevious.Values);

            _reconcilePrevious.Clear();

            return staleEntries;
        }
    }

    private sealed class TimedRailUnit : RailUnit
    {
        public TimedRailUnit(Grid header, TextBlock headerText, Canvas allDayCanvas, Canvas bodyCanvas)
        {
            Header = header;
            HeaderText = headerText;
            AllDayCanvas = allDayCanvas;
            BodyCanvas = bodyCanvas;
        }

        public Grid Header { get; }

        public TextBlock HeaderText { get; }

        public Canvas AllDayCanvas { get; }

        public Canvas BodyCanvas { get; }

        public int AllDayLaneCount { get; set; }
    }

    private sealed class MonthRailUnit : RailUnit
    {
        public MonthRailUnit(Canvas row, TextBlock[] dayLabels)
        {
            Row = row;
            DayLabels = dayLabels;
        }

        public Canvas Row { get; }

        public TextBlock[] DayLabels { get; }
    }
}
