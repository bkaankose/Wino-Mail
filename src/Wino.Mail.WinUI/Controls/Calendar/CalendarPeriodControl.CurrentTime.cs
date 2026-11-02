using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Wino.Calendar.Controls;

public sealed partial class CalendarPeriodControl
{
    private static readonly TimeSpan CurrentTimeRefreshInterval = TimeSpan.FromSeconds(30);
    private const double CurrentTimeLineHalfHeight = 4d;

    private DispatcherQueueTimer? _currentTimeTimer;
    private bool _isTimedScrollPositioned;

    private void StartCurrentTimeTimer()
    {
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
        var isVisible = !IsMonthRail &&
                        _space is not null &&
                        CalendarSettings?.IsCurrentTimeLineEnabled == true;

        CurrentTimeLine.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;

        if (isVisible)
        {
            CurrentTimeLine.Margin = new Thickness(0, Math.Max(0d, GetCurrentTimeOffset() - CurrentTimeLineHalfHeight), 0, 0);
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
