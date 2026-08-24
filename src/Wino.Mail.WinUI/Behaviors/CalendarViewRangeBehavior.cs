#nullable enable

using System;
using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;

namespace Wino.Mail.WinUI.Behaviors;

/// <summary>
/// Two-way binds a <see cref="CalendarView"/> to the calendar mode's visible range: the
/// range drives the selected dates, and picking a date navigates the calendar.
/// </summary>
/// <remarks>
/// The picker lives inside the navigation pane's virtualizing item host, so it unloads and
/// reloads as the pane scrolls, collapses or switches modes. Subscriptions are therefore
/// re-established on every load rather than once when the client is assigned: the attached
/// property only changes value the first time, so a load-time hook is the only thing that
/// survives recycling.
/// </remarks>
public static class CalendarViewRangeBehavior
{
    public static readonly DependencyProperty ClientProperty =
        DependencyProperty.RegisterAttached(
            "Client",
            typeof(ICalendarShellClient),
            typeof(CalendarViewRangeBehavior),
            new PropertyMetadata(null, OnClientChanged));

    public static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "State",
            typeof(object),
            typeof(CalendarViewRangeBehavior),
            new PropertyMetadata(null));

    public static ICalendarShellClient? GetClient(DependencyObject element)
        => (ICalendarShellClient?)element.GetValue(ClientProperty);

    public static void SetClient(DependencyObject element, ICalendarShellClient? value)
        => element.SetValue(ClientProperty, value);

    private static void OnClientChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not CalendarView calendarView)
            return;

        var state = GetOrCreateState(calendarView);

        state.Detach();
        state.Client = args.NewValue as ICalendarShellClient;

        // The element is usually already loaded by the time the binding resolves, so attach
        // right away instead of waiting for a Loaded that has already been raised.
        if (calendarView.IsLoaded)
        {
            state.Attach();
        }
    }

    private static CalendarViewRangeState GetOrCreateState(CalendarView calendarView)
    {
        if (calendarView.GetValue(StateProperty) is CalendarViewRangeState existing)
            return existing;

        var state = new CalendarViewRangeState(calendarView);
        calendarView.SetValue(StateProperty, state);

        calendarView.Loaded += (_, _) => state.Attach();
        calendarView.Unloaded += (_, _) => state.Detach();

        return state;
    }

    /// <summary>
    /// Per calendar view subscription bookkeeping. Holding the handlers here keeps subscribe
    /// and unsubscribe paired no matter how often the element is recycled.
    /// </summary>
    private sealed class CalendarViewRangeState(CalendarView calendarView)
    {
        private bool _isAttached;
        private bool _isSynchronizing;

        public ICalendarShellClient? Client { get; set; }

        public void Attach()
        {
            if (_isAttached || Client == null)
                return;

            _isAttached = true;
            Client.PropertyChanged += ClientPropertyChanged;
            calendarView.SelectedDatesChanged += SelectedDatesChanged;

            Synchronize();
        }

        public void Detach()
        {
            if (!_isAttached)
                return;

            _isAttached = false;

            if (Client != null)
            {
                Client.PropertyChanged -= ClientPropertyChanged;
            }

            calendarView.SelectedDatesChanged -= SelectedDatesChanged;
        }

        private void ClientPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is not (nameof(ICalendarShellClient.CurrentVisibleRange)
                or nameof(ICalendarShellClient.VisibleDateRangeText)))
            {
                return;
            }

            if (calendarView.DispatcherQueue.HasThreadAccess)
            {
                Synchronize();
            }
            else
            {
                calendarView.DispatcherQueue.TryEnqueue(Synchronize);
            }
        }

        private void Synchronize()
        {
            if (Client == null)
                return;

            _isSynchronizing = true;

            try
            {
                calendarView.FirstDayOfWeek = MapFirstDayOfWeek(
                    WinoApplication.Current.Services.GetRequiredService<IPreferencesService>().FirstDayOfWeek);

                calendarView.SelectedDates.Clear();

                VisibleDateRange? currentRange = Client.CurrentVisibleRange;

                if (currentRange == null)
                    return;

                foreach (var date in currentRange.Dates)
                {
                    calendarView.SelectedDates.Add(new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue)));
                }

                calendarView.SetDisplayDate(new DateTimeOffset(currentRange.AnchorDate.ToDateTime(TimeOnly.MinValue)));
            }
            finally
            {
                _isSynchronizing = false;
            }
        }

        private void SelectedDatesChanged(CalendarView sender, CalendarViewSelectedDatesChangedEventArgs args)
        {
            if (_isSynchronizing || Client == null)
                return;

            // With a multi-date selection, clicking a day inside the current range deselects
            // it, so a removal is just as much a navigation request as an addition.
            DateTimeOffset? interactedDate = args.AddedDates.Count > 0
                ? args.AddedDates[0]
                : args.RemovedDates.Count > 0 ? args.RemovedDates[0] : null;

            if (interactedDate is null)
                return;

            var clickedArgs = new CalendarViewDayClickedEventArgs(interactedDate.Value.DateTime);

            if (Client.DateClickedCommand.CanExecute(clickedArgs))
            {
                Client.DateClickedCommand.Execute(clickedArgs);
            }
        }

        private static Windows.Globalization.DayOfWeek MapFirstDayOfWeek(DayOfWeek dayOfWeek)
            => dayOfWeek switch
            {
                DayOfWeek.Sunday => Windows.Globalization.DayOfWeek.Sunday,
                DayOfWeek.Monday => Windows.Globalization.DayOfWeek.Monday,
                DayOfWeek.Tuesday => Windows.Globalization.DayOfWeek.Tuesday,
                DayOfWeek.Wednesday => Windows.Globalization.DayOfWeek.Wednesday,
                DayOfWeek.Thursday => Windows.Globalization.DayOfWeek.Thursday,
                DayOfWeek.Friday => Windows.Globalization.DayOfWeek.Friday,
                DayOfWeek.Saturday => Windows.Globalization.DayOfWeek.Saturday,
                _ => Windows.Globalization.DayOfWeek.Monday
            };
    }
}
