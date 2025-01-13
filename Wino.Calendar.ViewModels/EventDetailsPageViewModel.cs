using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Calendar;
using Wino.Core.Domain.Models.Navigation;
using Wino.Core.ViewModels;
using Wino.Messaging.Client.Calendar;

namespace Wino.Calendar.ViewModels
{
    public partial class EventDetailsPageViewModel : CalendarBaseViewModel
    {
        private readonly ICalendarService _calendarService;
        private readonly INativeAppService _nativeAppService;
        private readonly IPreferencesService _preferencesService;

        public CalendarSettings CurrentSettings { get; }

        #region Details

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanViewSeries))]
        private CalendarItemViewModel _currentEvent;

        public bool CanViewSeries => CurrentEvent?.CalendarItem.RecurringCalendarItemId != null;

        #endregion

        public EventDetailsPageViewModel(ICalendarService calendarService, INativeAppService nativeAppService, IPreferencesService preferencesService)
        {
            _calendarService = calendarService;
            _nativeAppService = nativeAppService;
            _preferencesService = preferencesService;

            CurrentSettings = _preferencesService.GetCurrentCalendarSettings();
        }

        public override void OnNavigatedTo(NavigationMode mode, object parameters)
        {
            base.OnNavigatedTo(mode, parameters);

            Messenger.Send(new DetailsPageStateChangedMessage(true));

            if (parameters == null || parameters is not CalendarItemViewModel passedCalendarItem)
                return;

            CurrentEvent = passedCalendarItem;
        }

        public override void OnNavigatedFrom(NavigationMode mode, object parameters)
        {
            base.OnNavigatedFrom(mode, parameters);

            Messenger.Send(new DetailsPageStateChangedMessage(false));
        }

        [RelayCommand]
        private async Task SaveAsync()
        {

        }

        [RelayCommand]
        private async Task DeleteAsync()
        {

        }

        [RelayCommand]
        private Task JoinOnline()
        {
            if (CurrentEvent == null || string.IsNullOrEmpty(CurrentEvent.CalendarItem.HtmlLink)) return Task.CompletedTask;

            return _nativeAppService.LaunchUriAsync(new Uri(CurrentEvent.CalendarItem.HtmlLink));
        }

        [RelayCommand]
        private async Task Respond(CalendarItemStatus status)
        {
            if (CurrentEvent == null) return;
        }
    }
}
