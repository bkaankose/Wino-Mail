using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Calendar.Models.CalendarTypeStrategies;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.MenuItems;
using Wino.Core.ViewModels;
using Wino.Messaging.Client.Calendar;

namespace Wino.Calendar.ViewModels
{
    public partial class CalendarPageViewModel : CalendarBaseViewModel,
        IRecipient<CalendarInitializeMessage>
    {
        [ObservableProperty]
        private ObservableRangeCollection<DayRangeRenderModel> _dayRanges = [];

        [ObservableProperty]
        private int _selectedDateRangeIndex;

        [ObservableProperty]
        private bool _isCalendarEnabled = true;

        private CalendarSettings _calendarSettings;

        private CalendarInitializeMessage _latestInitOptions;

        public CalendarPageViewModel()
        {
            _calendarSettings = new CalendarSettings()
            {
                DayHeaderDisplayType = DayHeaderDisplayType.TwentyFourHour,
                FirstDayOfWeek = DayOfWeek.Monday,
                WorkingDays = new List<DayOfWeek>() { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday },
                WorkingHourStart = new TimeSpan(8, 0, 0),
                WorkingHourEnd = new TimeSpan(16, 0, 0),
                HourHeight = 60,
            };
        }

        public override async void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);

            if (parameters is CalendarPageNavigationArgs args)
            {
                if (args.RequestDefaultNavigation)
                    await PerformDefaultNavigationOptions();
            }
        }

        private Task PerformDefaultNavigationOptions()
        {
            Messenger.Send(new ClickCalendarDateMessage(DateTime.Now.Date));

            return Task.CompletedTask;
        }

        private BaseCalendarTypeDrawingStrategy GetDrawingStrategy(CalendarDisplayType displayType)
        {
            return displayType switch
            {
                CalendarDisplayType.Day => new DayCalendarDrawingStrategy(_calendarSettings),
                CalendarDisplayType.Week => new WeekCalendarDrawingStrategy(_calendarSettings),
                _ => throw new NotImplementedException(),
            };
        }

        private bool ShouldResetDayRanges(CalendarInitializeMessage message)
        {
            // 1. Display type is different.
            // 2. Day display count is different.
            // 3. Display date is not in the visible range.

            return
                _latestInitOptions != null &&
                (_latestInitOptions.DisplayType != message.DisplayType ||
                _latestInitOptions.DayDisplayCount != message.DayDisplayCount ||
                (DayRanges != null && !DayRanges.Select(a => a.CalendarRenderOptions).Any(b => b.DateRange.IsInRange(message.DisplayDate))));
        }

        public void Receive(CalendarInitializeMessage message)
        {
            if (ShouldResetDayRanges(message))
            {
                DayRanges.Clear();

                Debug.WriteLine("Resetting day ranges.");
            }

            var loadDirection = GetLoadDirection(message.DisplayDate);

            if (loadDirection == CalendarLoadDirection.None)
            {
                // Scroll to the selected date.

                Messenger.Send(new ScrollToDateMessage(message.DisplayDate));
                Debug.WriteLine("Scrolling to selected date.");
                return;
            }
            else
            {
                RenderDates(message, loadDirection);
            }
        }

        private void RenderDates(CalendarInitializeMessage message, CalendarLoadDirection direction)
        {
            // This is the part we arrange the flip view calendar logic.

            /* Loading for a month of the selected date is fine.
             * If the selected date is in the loaded range, we'll just change the selected flip index to scroll.
             * If the selected date is not in the loaded range:
             * 1. Detect the direction of the scroll.
             * 2. Load the next month.
             * 3. Replace existing month with the new month.
             */

            // 2 things are important: How many items should 1 flip have, and, where we should start loading?

            var strategy = GetDrawingStrategy(message.DisplayType);

            int eachFlipItemCount = strategy.GetRenderDayCount(message.DisplayDate, message.DayDisplayCount);
            DateRange flipLoadRange = strategy.GetRenderDateRange(message.DisplayDate, message.DayDisplayCount);

            // Create day ranges for each flip item until we reach the total days to load.
            int totalFlipItemCount = (int)Math.Ceiling((double)flipLoadRange.TotalDays / eachFlipItemCount);

            List<DayRangeRenderModel> renderModels = new();

            for (int i = 0; i < totalFlipItemCount; i++)
            {
                var startDate = flipLoadRange.StartDate.AddDays(i * eachFlipItemCount);
                var endDate = startDate.AddDays(eachFlipItemCount);

                var range = new DateRange(startDate, endDate);
                var renderOptions = new CalendarRenderOptions(range, _calendarSettings);

                renderModels.Add(new DayRangeRenderModel(renderOptions));
            }

            DayRanges.ReplaceRange(renderModels);

            _latestInitOptions = message;

            Debug.WriteLine($"Flip count: ({DayRanges.Count})");
            foreach (var item in DayRanges)
            {
                Debug.WriteLine($"- {item.CalendarRenderOptions.DateRange.ToString()}");
            }

            Messenger.Send(new ScrollToDateMessage(message.DisplayDate));
        }

        private CalendarLoadDirection GetLoadDirection(DateTime selectedDate)
        {
            if (DayRanges.Count == 0) return CalendarLoadDirection.Next;

            var firstRange = DayRanges[0];
            var lastRange = DayRanges[DayRanges.Count - 1];

            if (selectedDate < firstRange.CalendarDays[0].RepresentingDate)
            {
                return CalendarLoadDirection.Previous;
            }
            else if (selectedDate > lastRange.CalendarDays[lastRange.CalendarDays.Count - 1].RepresentingDate)
            {
                return CalendarLoadDirection.Next;
            }
            return CalendarLoadDirection.None;
        }
    }
}
