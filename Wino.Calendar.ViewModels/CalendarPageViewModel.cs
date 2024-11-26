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
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Calendar.CalendarTypeStrategies;
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

        // Get rid of some of the items if we have too many.
        private const int maxDayRangeSize = 10;
        private readonly IPreferencesService _preferencesService;

        // Store latest rendered options.
        private CalendarDisplayType _currentDisplayType;
        private int _displayDayCount;

        private SemaphoreSlim _calendarLoadingSemaphore = new(1);
        private bool isLoadMoreBlocked = false;
        private CalendarSettings _currentSettings = null;

        public IStatePersistanceService StatePersistanceService { get; }

        public CalendarPageViewModel(IStatePersistanceService statePersistanceService,
                                     IPreferencesService preferencesService)
        {
            StatePersistanceService = statePersistanceService;
            _preferencesService = preferencesService;

            _currentSettings = _preferencesService.GetCurrentCalendarSettings();
        }

        public override void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);

            Messenger.Send(new CalendarInitializedMessage());
        }

        // TODO: Replace when calendar settings are updated.
        // Should be a field ideally.
        private BaseCalendarTypeDrawingStrategy GetDrawingStrategy(CalendarDisplayType displayType)
        {
            return displayType switch
            {
                CalendarDisplayType.Day => new DayCalendarDrawingStrategy(_currentSettings),
                CalendarDisplayType.Week => new WeekCalendarDrawingStrategy(_currentSettings),
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
                (_currentDisplayType != StatePersistanceService.CalendarDisplayType ||
                _displayDayCount != StatePersistanceService.DayDisplayCount ||
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

                    Debug.WriteLine("Will reset day ranges.");
                }
                else if (ShouldScrollToItem(message))
                {
                    // Scroll to the selected date.

                    Messenger.Send(new ScrollToDateMessage(message.DisplayDate));
                    Debug.WriteLine("Scrolling to selected date.");
                    return;
                }

                // This will replace the whole collection because the user initiated a new render.
                await RenderDatesAsync(message.CalendarInitInitiative,
                                       message.DisplayDate,
                                       CalendarLoadDirection.Replace);
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

        private async Task RenderDatesAsync(CalendarInitInitiative calendarInitInitiative,
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

            var strategy = GetDrawingStrategy(StatePersistanceService.CalendarDisplayType);
            var displayDate = loadingDisplayDate.GetValueOrDefault();

            // How many days should be placed in 1 flip view item?
            int eachFlipItemCount = strategy.GetRenderDayCount(displayDate, StatePersistanceService.DayDisplayCount);

            DateRange flipLoadRange = null;

            if (calendarInitInitiative == CalendarInitInitiative.User)
            {
                flipLoadRange = strategy.GetRenderDateRange(displayDate, StatePersistanceService.DayDisplayCount);
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
                    flipLoadRange = strategy.GetPreviousDateRange(currentInitializedDateRange, StatePersistanceService.DayDisplayCount);
                }
                else
                {
                    flipLoadRange = strategy.GetNextDateRange(currentInitializedDateRange, StatePersistanceService.DayDisplayCount);
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
                var renderOptions = new CalendarRenderOptions(range, _currentSettings);

                renderModels.Add(new DayRangeRenderModel(renderOptions));
            }

            CalendarLoadDirection animationDirection = calendarLoadDirection;

            bool removeCurrent = calendarLoadDirection == CalendarLoadDirection.Replace;

            if (calendarLoadDirection == CalendarLoadDirection.Replace)
            {
                // New date ranges are being replaced.
                // We must preserve existing selection if any, add the items before/after the current one, remove the current one.
                // This will make sure the new dates are animated in the correct direction.

                isLoadMoreBlocked = true;

                // Remove all other dates except this one.

                await ExecuteUIThread(() =>
                {
                    DayRanges.RemoveRange(DayRanges.Where(a => a != SelectedDayRange).ToList());
                });

                animationDirection = displayDate <= SelectedDayRange?.CalendarRenderOptions.DateRange.StartDate ?
                    CalendarLoadDirection.Previous : CalendarLoadDirection.Next;
            }

            if (animationDirection == CalendarLoadDirection.Next)
            {
                await ExecuteUIThread(() =>
                {
                    foreach (var item in renderModels)
                    {
                        DayRanges.Add(item);
                    }
                });
            }
            else if (animationDirection == CalendarLoadDirection.Previous)
            {
                // Wait for the animation to finish.
                // Otherwise it somehow shutters a little, which is annoying.

                if (!removeCurrent) await Task.Delay(500);

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

            if (removeCurrent)
            {
                await ExecuteUIThread(() =>
                {
                    DayRanges.Remove(SelectedDayRange);
                });
            }

            // TODO...
            // await TryConsolidateItemsAsync();

            isLoadMoreBlocked = false;

            // Only scroll if the render is initiated by user.
            // Otherwise we'll scroll to the app rendered invisible date range.
            if (calendarInitInitiative == CalendarInitInitiative.User)
            {
                // Save the current settings for the page for later comparison.
                _currentDisplayType = StatePersistanceService.CalendarDisplayType;
                _displayDayCount = StatePersistanceService.DayDisplayCount;

                Messenger.Send(new ScrollToDateMessage(displayDate));
            }
        }

        private async Task TryConsolidateItemsAsync()
        {
            // Check if trimming is necessary
            if (DayRanges.Count > maxDayRangeSize)
            {
                Debug.WriteLine("Trimming items.");

                isLoadMoreBlocked = true;

                var removeCount = DayRanges.Count - maxDayRangeSize;

                await Task.Delay(500);

                // Right shifted, remove from the start.
                if (SelectedDateRangeIndex > DayRanges.Count / 2)
                {
                    DayRanges.RemoveRange(DayRanges.Take(removeCount).ToList());
                }
                else
                {
                    // Left shifted, remove from the end.
                    DayRanges.RemoveRange(DayRanges.Skip(DayRanges.Count - removeCount).Take(removeCount));
                }

                SelectedDateRangeIndex = DayRanges.IndexOf(SelectedDayRange);
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

        partial void OnSelectedDayRangeChanged(DayRangeRenderModel value)
        {
            if (DayRanges.Count == 0 || SelectedDateRangeIndex < 0) return;

            if (isLoadMoreBlocked) return;

            var selectedRange = DayRanges[SelectedDateRangeIndex];

            if (selectedRange != null)
            {
                // Send the loading message initiated by the app.
                if (SelectedDateRangeIndex == DayRanges.Count - 1)
                {
                    // Load next, starting from the end date.
                    _ = LoadMoreAsync(CalendarLoadDirection.Next);
                }
                else if (SelectedDateRangeIndex == 0)
                {
                    // Load previous, starting from the start date.

                    _ = LoadMoreAsync(CalendarLoadDirection.Previous);
                }
            }
        }

        private async Task LoadMoreAsync(CalendarLoadDirection direction)
        {
            Debug.WriteLine($"Loading {direction} items.");

            try
            {
                await _calendarLoadingSemaphore.WaitAsync();
                await RenderDatesAsync(CalendarInitInitiative.App, calendarLoadDirection: direction);
            }
            catch (Exception)
            {
                Debugger.Break();
            }
            finally
            {
                _calendarLoadingSemaphore.Release();
            }
        }

        protected override async void OnCalendarEventAdded(ICalendarItem calendarItem)
        {
            base.OnCalendarEventAdded(calendarItem);

            // Test
            var eventDays = DayRanges.SelectMany(a => a.CalendarDays).Where(b => b.Period.Start.Date == calendarItem.StartTime.Date);

            var beforeAllDay = new CalendarItem(calendarItem.StartTime.Date.AddHours(0), calendarItem.StartTime.Date.AddMinutes(30))
            {
                Name = "kj"
            };

            var allday = new CalendarItem(calendarItem.StartTime.Date.AddHours(1), calendarItem.StartTime.Date.AddHours(10).AddMinutes(59))
            {
                Name = "All day"
            };

            var test = new CalendarItem(calendarItem.StartTime.Date.AddHours(4), calendarItem.StartTime.Date.AddHours(4).AddMinutes(30))
            {
                Name = "test"
            };

            var hour = new CalendarItem(calendarItem.StartTime.Date.AddHours(7), calendarItem.StartTime.Date.AddHours(8))
            {
                Name = "1 h"
            };

            var hourandhalf = new CalendarItem(calendarItem.StartTime.Date.AddHours(7), calendarItem.StartTime.Date.AddHours(8).AddMinutes(30))
            {
                Name = "1.5 h"
            };
            var halfhour1 = new CalendarItem(calendarItem.StartTime.Date.AddHours(7), calendarItem.StartTime.Date.AddHours(7).AddMinutes(30))
            {
                Name = "30 min"
            };

            var halfhour2 = new CalendarItem(calendarItem.StartTime.Date.AddHours(7).AddMinutes(30), calendarItem.StartTime.Date.AddHours(8))
            {
                Name = "30 min"
            };
            var halfhour3 = new CalendarItem(calendarItem.StartTime.Date.AddHours(8), calendarItem.StartTime.Date.AddHours(8).AddMinutes(30))
            {
                Name = "30 min"
            };

            foreach (var day in eventDays)
            {
                await ExecuteUIThread(() =>
                {
                    day.Events.Add(beforeAllDay);
                    day.Events.Add(allday);
                    day.Events.Add(hourandhalf);
                    day.Events.Add(hour);
                    day.Events.Add(halfhour1);
                    day.Events.Add(halfhour2);
                    day.Events.Add(halfhour3);
                    day.Events.Add(test);
                });
            }

            return;
        }
    }
}
