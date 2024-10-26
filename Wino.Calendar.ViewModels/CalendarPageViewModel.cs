using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Diagnostics;
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
        private DayRangeRenderModel _selectedDayRange;

        [ObservableProperty]
        private bool _isCalendarEnabled = true;

        private CalendarSettings _calendarSettings;

        // Store latest rendered options.
        private CalendarDisplayType _currentDisplayType;
        private int _displayDayCount;

        private SemaphoreSlim _calendarLoadingSemaphore = new(1);

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

        partial void OnIsCalendarEnabledChanging(bool oldValue, bool newValue) => Messenger.Send(new CalendarEnableStatusChangedMessage(newValue));

        private bool ShouldResetDayRanges(CalendarInitializeMessage message)
        {
            // Never reset if the initiative is from the app.
            if (message.CalendarInitInitiative == CalendarInitInitiative.App) return false;

            // 1. Display type is different.
            // 2. Day display count is different.
            // 3. Display date is not in the visible range.

            return
                (_currentDisplayType != message.DisplayType ||
                _displayDayCount != message.DayDisplayCount ||
                (DayRanges != null && !DayRanges.Select(a => a.CalendarRenderOptions).Any(b => b.DateRange.IsInRange(message.DisplayDate))));
        }

        public async void Receive(CalendarInitializeMessage message)
        {
            await _calendarLoadingSemaphore.WaitAsync();

            try
            {
                await ExecuteUIThread(() => IsCalendarEnabled = false);

                if (ShouldResetDayRanges(message))
                {
                    DayRanges.Clear();

                    Debug.WriteLine("Resetting day ranges.");
                }

                if (ShouldScrollToItem(message))
                {
                    // Scroll to the selected date.

                    Messenger.Send(new ScrollToDateMessage(message.DisplayDate));
                    Debug.WriteLine("Scrolling to selected date.");
                    return;
                }

                // Either app is trying to load new messages or user clicked to some date range.
                await RenderDatesAsync(message.DisplayType, message.DayDisplayCount, message.CalendarInitInitiative, message.DisplayDate, CalendarLoadDirection.Replace);
            }
            catch (Exception ex)
            {
                Debugger.Break();
            }
            finally
            {
                _calendarLoadingSemaphore.Release();

                await ExecuteUIThread(() => IsCalendarEnabled = true);
            }
        }

        private async Task RenderDatesAsync(CalendarDisplayType displayType,
                                            int dayDisplayCount,
                                            CalendarInitInitiative calendarInitInitiative,
                                            DateTime? loadingDisplayDate = null,
                                            CalendarLoadDirection calendarLoadDirection = CalendarLoadDirection.Replace)
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

            // User initiated renders must always have a date to start with.
            if (calendarInitInitiative == CalendarInitInitiative.User) Guard.IsNotNull(loadingDisplayDate, nameof(loadingDisplayDate));

            var strategy = GetDrawingStrategy(displayType);
            var displayDate = loadingDisplayDate.GetValueOrDefault();

            // How many days should be placed in 1 flip view item?
            int eachFlipItemCount = strategy.GetRenderDayCount(displayDate, dayDisplayCount);

            DateRange flipLoadRange = null;

            if (calendarInitInitiative == CalendarInitInitiative.User)
            {
                flipLoadRange = strategy.GetRenderDateRange(displayDate, dayDisplayCount);
            }
            else
            {
                var minimumLoadedDate = DayRanges[0].CalendarRenderOptions.DateRange.StartDate;
                var maximumLoadedDate = DayRanges[DayRanges.Count - 1].CalendarRenderOptions.DateRange.EndDate;

                var currentInitializedDateRange = new DateRange(minimumLoadedDate, maximumLoadedDate);

                // App is trying to load.
                // This should be based on direction. We'll load the next or previous range.
                // DisplayDate is either the start or end date of the current visible range.

                if (calendarLoadDirection == CalendarLoadDirection.Previous)
                {
                    flipLoadRange = strategy.GetPreviousDateRange(currentInitializedDateRange, dayDisplayCount);
                }
                else
                {
                    flipLoadRange = strategy.GetNextDateRange(currentInitializedDateRange, dayDisplayCount);
                }
            }

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

            if (calendarLoadDirection == CalendarLoadDirection.Replace)
            {
                // User initiated, not in the visible range.
                // Replace whole collection.

                await ExecuteUIThread(() =>
                {
                    DayRanges.ReplaceRange(renderModels);
                });
            }
            else if (calendarLoadDirection == CalendarLoadDirection.Next)
            {
                await ExecuteUIThread(() =>
                {
                    foreach (var item in renderModels)
                    {
                        DayRanges.Add(item);
                    }
                });
            }
            else if (calendarLoadDirection == CalendarLoadDirection.Previous)
            {
                // Insert each render model in reverse order.

                for (int i = renderModels.Count - 1; i >= 0; i--)
                {
                    await ExecuteUIThread(() =>
                    {
                        DayRanges.Insert(0, renderModels[i]);
                    });
                }
            }

            Debug.WriteLine($"Flip count: ({DayRanges.Count})");

            foreach (var item in DayRanges)
            {
                Debug.WriteLine($"- {item.CalendarRenderOptions.DateRange.ToString()}");
            }

            // Only scroll if the render is initiated by user.
            // Otherwise we'll scroll to the app rendered invisible date range.
            if (calendarInitInitiative == CalendarInitInitiative.User)
            {
                // Save the current settings.
                _currentDisplayType = displayType;
                _displayDayCount = dayDisplayCount;

                Messenger.Send(new ScrollToDateMessage(displayDate));
            }
        }

        private bool ShouldScrollToItem(CalendarInitializeMessage message)
        {
            // Never scroll if the initiative is from the app.
            if (message.CalendarInitInitiative == CalendarInitInitiative.App) return false;

            // Nothing to scroll.
            if (DayRanges.Count == 0) return false;

            var minimumLoadedDate = DayRanges[0].CalendarRenderOptions.DateRange.StartDate;
            var maximumLoadedDate = DayRanges[DayRanges.Count - 1].CalendarRenderOptions.DateRange.EndDate;

            var selectedDate = message.DisplayDate;

            return selectedDate >= minimumLoadedDate && selectedDate <= maximumLoadedDate;
        }

        private CalendarLoadDirection GetLoadDirection(CalendarInitializeMessage message)
        {
            // If the direction is trying to be set by the app, we should cancel the operation.
            // We'll render the date range without any scroll.

            if (DayRanges.Count == 0) return CalendarLoadDirection.Next;

            var firstRange = DayRanges[0];
            var lastRange = DayRanges[DayRanges.Count - 1];

            if (message.DisplayDate < firstRange.CalendarDays[0].RepresentingDate)
            {
                return CalendarLoadDirection.Previous;
            }

            return CalendarLoadDirection.Next;
        }

        partial void OnSelectedDayRangeChanged(DayRangeRenderModel value)
        {
            if (DayRanges.Count == 0 || SelectedDateRangeIndex < 0) return;

            var selectedRange = DayRanges[SelectedDateRangeIndex];

            if (selectedRange != null)
            {
                // Send the loading message initiated by the app.

                CalendarInitializeMessage args = null;
                if (SelectedDateRangeIndex == DayRanges.Count - 1)
                {
                    // Load next, starting from the end date.
                    _ = LoadMoreAsync(CalendarLoadDirection.Next);


                }
                else if (SelectedDateRangeIndex == 0)
                {
                    // Load previous, starting from the start date.

                    _ = LoadMoreAsync(CalendarLoadDirection.Previous);

                    //args = new CalendarInitializeMessage(displayType,
                    //                 selectedRange.CalendarRenderOptions.DateRange.StartDate,
                    //                 _latestInitOptions.DayDisplayCount,
                    //                 CalendarInitInitiative.App);

                    Debug.WriteLine("Loading previous items.");
                }

                //if (args != null)
                //{
                //    Task.Delay(500).ContinueWith((t) => { Messenger.Send(args); });
                //}
            }
        }

        private async Task LoadMoreAsync(CalendarLoadDirection direction)
        {
            Debug.WriteLine($"Loading {direction} items.");

            try
            {
                await _calendarLoadingSemaphore.WaitAsync();
                await RenderDatesAsync(_currentDisplayType, _displayDayCount, CalendarInitInitiative.App, calendarLoadDirection: direction);
            }
            catch (Exception)
            {

            }
            finally
            {
                _calendarLoadingSemaphore.Release();
            }
        }
    }
}
