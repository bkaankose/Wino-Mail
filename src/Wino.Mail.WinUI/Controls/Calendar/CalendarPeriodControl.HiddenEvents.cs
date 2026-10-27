using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Calendar.Controls;

public sealed partial class CalendarPeriodControl
{
    private const double HiddenEventVisibleTolerance = 2d;

    private const double HiddenEventRevealMargin = 28d;

    private CalendarHiddenEventsIndicator CreateHiddenEventsIndicator(bool isEarlier)
    {
        var indicator = new CalendarHiddenEventsIndicator
        {
            VerticalAlignment = isEarlier ? VerticalAlignment.Top : VerticalAlignment.Bottom,
            Margin = new Thickness(2d, 4d, 2d, 4d)
        };

        indicator.SetDirection(isEarlier);
        indicator.Invoked += HiddenEventsIndicatorInvoked;

        return indicator;
    }

    private void UpdateHiddenEventIndicators()
    {
        if (_space is not CalendarRailSpace space || IsMonthRail || _areCanvasesReleased)
        {
            return;
        }

        var viewportHeight = TimedScrollViewer.ViewportHeight;
        var viewportTop = TimedScrollViewer.VerticalOffset;
        var viewportBottom = viewportTop + viewportHeight;
        var firstVisibleUnit = (int)Math.Floor(_position);
        var lastVisibleUnit = (int)Math.Ceiling(_position + space.VisibleUnitCount) - 1;
        var culture = CalendarSettings?.CultureInfo;

        foreach (var unit in _units.Values)
        {
            if (unit is not TimedRailUnit timedUnit || timedUnit.Dates.Count == 0)
            {
                continue;
            }

            var isInViewport = viewportHeight > 0d && unit.Index >= firstVisibleUnit && unit.Index <= lastVisibleUnit;
            var earlierCount = isInViewport ? timedUnit.CountEndingAtOrBefore(viewportTop + HiddenEventVisibleTolerance) : 0;
            var laterCount = isInViewport
                ? timedUnit.SpansByTop.Count - timedUnit.FindFirstStartingAtOrAfter(viewportBottom - HiddenEventVisibleTolerance)
                : 0;
            var date = timedUnit.Dates[0];

            ShowHiddenEventsCount(timedUnit.EarlierIndicator, earlierCount, date, culture);
            ShowHiddenEventsCount(timedUnit.LaterIndicator, laterCount, date, culture);
        }
    }

    private void ShowHiddenEventsCount(CalendarHiddenEventsIndicator indicator, int count, DateOnly date, CultureInfo? culture)
    {
        // Collapsing the focused indicator would drop keyboard focus out of the calendar.
        if (count == 0 && indicator.Visibility == Visibility.Visible && indicator.HasFocus)
        {
            Focus(FocusState.Programmatic);
        }

        indicator.ShowCount(count, date, culture);
    }

    private void HideHiddenEventsIndicator(CalendarHiddenEventsIndicator indicator)
        => ShowHiddenEventsCount(indicator, 0, default, null);

    private void HiddenEventsIndicatorInvoked(object? sender, EventArgs e)
    {
        if (sender is not CalendarHiddenEventsIndicator indicator)
        {
            return;
        }

        foreach (var unit in _units.Values)
        {
            if (unit is TimedRailUnit timedUnit &&
                (ReferenceEquals(timedUnit.EarlierIndicator, indicator) || ReferenceEquals(timedUnit.LaterIndicator, indicator)))
            {
                RevealNearestHiddenEvent(timedUnit, indicator.IsEarlier);
                return;
            }
        }
    }

    private void RevealNearestHiddenEvent(TimedRailUnit unit, bool isEarlier)
    {
        var viewportHeight = TimedScrollViewer.ViewportHeight;
        var viewportTop = TimedScrollViewer.VerticalOffset;
        double targetOffset;

        if (isEarlier)
        {
            var hiddenCount = unit.CountEndingAtOrBefore(viewportTop + HiddenEventVisibleTolerance);

            if (hiddenCount == 0)
            {
                return;
            }

            var nearest = unit.SpansByBottom[hiddenCount - 1];

            targetOffset = nearest.Top - HiddenEventRevealMargin;
        }
        else
        {
            var firstHiddenIndex = unit.FindFirstStartingAtOrAfter(viewportTop + viewportHeight - HiddenEventVisibleTolerance);

            if (firstHiddenIndex >= unit.SpansByTop.Count)
            {
                return;
            }

            var nearest = unit.SpansByTop[firstHiddenIndex];

            targetOffset = Math.Min(nearest.Bottom + HiddenEventRevealMargin - viewportHeight, nearest.Top - HiddenEventRevealMargin);
        }

        TimedScrollViewer.ChangeView(
            null,
            Math.Clamp(targetOffset, 0d, Math.Max(0d, TimedScrollViewer.ScrollableHeight)),
            null,
            !_uiSettings.AnimationsEnabled);
    }

    private void TimedScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => UpdateHiddenEventIndicators();

    private void TimedHiddenEventsHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var height = e.NewSize.Height;

        foreach (var unit in _units.Values)
        {
            if (unit is TimedRailUnit timedUnit)
            {
                timedUnit.HiddenEventsColumn.Height = height;
            }
        }

        UpdateRailClips();
        UpdateHiddenEventIndicators();
    }

    /// <summary>
    /// The indicators sit outside the hour ScrollViewer, so a plain vertical wheel over one is passed
    /// on to the hours. Shift and horizontal wheels bubble on to the rail pan.
    /// </summary>
    private void TimedHiddenEventsHostPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;

        if (properties.IsHorizontalMouseWheel || (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0)
        {
            return;
        }

        TimedScrollViewer.ChangeView(
            null,
            Math.Clamp(TimedScrollViewer.VerticalOffset - properties.MouseWheelDelta, 0d, Math.Max(0d, TimedScrollViewer.ScrollableHeight)),
            null);
        e.Handled = true;
    }

    private readonly record struct TimedSpan(double Top, double Bottom);
}
