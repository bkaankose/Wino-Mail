using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Mail.WinUI.Controls.Calendar;

public sealed partial class CalendarPeriodControl
{
    private static readonly TimeSpan CurrentTimeRefreshInterval = TimeSpan.FromSeconds(30);
    private const double CurrentTimeLineHalfHeight = 4d;
    private const byte TodayFillAlpha = 26;
    private const float TodayHeaderBarHeight = 2f;

    private DispatcherQueueTimer? _currentTimeTimer;
    private bool _isTimedScrollPositioned;
    private DateOnly _today = DateOnly.FromDateTime(DateTime.Now);

    private void StartCurrentTimeTimer()
    {
        if (_currentTimeTimer is not null)
        {
            return;
        }

        _currentTimeTimer = DispatcherQueue.CreateTimer();
        _currentTimeTimer.Interval = CurrentTimeRefreshInterval;
        _currentTimeTimer.IsRepeating = true;
        _currentTimeTimer.Tick += CurrentTimeTimerTick;
        _currentTimeTimer.Start();
    }

    private void StopCurrentTimeTimer()
    {
        if (_currentTimeTimer is null)
        {
            return;
        }

        _currentTimeTimer.Stop();
        _currentTimeTimer.Tick -= CurrentTimeTimerTick;
        _currentTimeTimer = null;
    }

    private void CurrentTimeTimerTick(DispatcherQueueTimer sender, object args) => UpdateCurrentTimeLine();

    private double GetCurrentTimeOffset() => DateTime.Now.TimeOfDay.TotalHours * GetHourHeight();

    private void UpdateCurrentTimeLine()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);

        if (today != _today)
        {
            _today = today;
            ApplyTodayHeaderStyles();
            InvalidateAllCanvases();
        }

        var todayUnit = 0;
        var isVisible = !IsMonthRail &&
                        _space is CalendarRailSpace space &&
                        _unitSize > 0d &&
                        CalendarSettings?.IsCurrentTimeLineEnabled == true &&
                        space.GetUnitStartDate(todayUnit = space.GetUnitIndex(today)) == today &&
                        todayUnit >= _windowStart &&
                        todayUnit < _windowStart + _windowCount;

        CurrentTimeLine.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;

        if (isVisible)
        {
            Canvas.SetLeft(CurrentTimeLine, (todayUnit - _originUnit) * _unitSize);
            Canvas.SetTop(CurrentTimeLine, Math.Max(0d, GetCurrentTimeOffset() - CurrentTimeLineHalfHeight));
            CurrentTimeLine.Width = _unitSize;
        }
    }

    private void ApplyTodayHeaderStyles()
    {
        foreach (var unit in _units.Values)
        {
            if (unit is TimedRailUnit timedUnit)
            {
                ApplyTodayHeaderStyle(timedUnit);
            }
        }
    }

    [DynamicWindowsRuntimeCast(typeof(Style))]
    private void ApplyTodayHeaderStyle(TimedRailUnit unit)
    {
        if (unit.Dates.Count > 0 && unit.Dates[0] == _today)
        {
            unit.HeaderText.Style = (Style)Resources["TodayTimedHeaderTextStyle"];
        }
        else
        {
            unit.HeaderText.ClearValue(FrameworkElement.StyleProperty);
        }
    }

    /// <summary>
    /// Centres the hours on the current time the first time the timed surface is shown, and again
    /// after the month view, so that opening the calendar does not start at midnight. Later layouts
    /// keep the user's scroll position.
    /// </summary>
    private void PositionTimedScrollOnce()
    {
        if (_isTimedScrollPositioned)
        {
            return;
        }

        _isTimedScrollPositioned = true;

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_areCanvasesReleased || IsMonthRail)
            {
                return;
            }

            TimedScrollViewer.UpdateLayout();

            var target = GetCurrentTimeOffset() - (TimedScrollViewer.ViewportHeight / 2d);
            TimedScrollViewer.ChangeView(
                null,
                Math.Clamp(target, 0d, Math.Max(0d, TimedScrollViewer.ScrollableHeight)),
                null,
                disableAnimation: true);
        });
    }
}
