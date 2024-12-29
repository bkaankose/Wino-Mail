using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Calendar.ViewModels.Data
{
    public partial class GroupedAccountCalendarViewModel : ObservableObject
    {
        public MailAccount Account { get; }
        public ObservableCollection<AccountCalendarViewModel> AccountCalendars { get; }

        public GroupedAccountCalendarViewModel(MailAccount account, IEnumerable<AccountCalendarViewModel> calendarViewModels)
        {
            Account = account;
            AccountCalendars = new ObservableCollection<AccountCalendarViewModel>(calendarViewModels);

            ManageIsCheckedState();

            foreach (var calendarViewModel in calendarViewModels)
            {
                calendarViewModel.PropertyChanged += CalendarPropertyChanged;
            }

            AccountCalendars.CollectionChanged += CalendarListUpdated;
        }

        private void CalendarListUpdated(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
            {
                foreach (AccountCalendarViewModel calendar in e.NewItems)
                {
                    calendar.PropertyChanged += CalendarPropertyChanged;
                }
            }
            else if (e.Action == NotifyCollectionChangedAction.Remove)
            {
                foreach (AccountCalendarViewModel calendar in e.OldItems)
                {
                    calendar.PropertyChanged -= CalendarPropertyChanged;
                }
            }
            else if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (AccountCalendarViewModel calendar in e.OldItems)
                {
                    calendar.PropertyChanged -= CalendarPropertyChanged;
                }
            }
        }

        private void CalendarPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is AccountCalendarViewModel viewModel)
            {
                if (e.PropertyName == nameof(AccountCalendarViewModel.IsChecked))
                {
                    ManageIsCheckedState();
                }
            }
        }

        [ObservableProperty]
        private bool _isExpanded = true;

        [ObservableProperty]
        private bool? isCheckedState = true;

        private bool _isExternalPropChangeBlocked = false;

        private void ManageIsCheckedState()
        {
            _isExternalPropChangeBlocked = true;

            if (AccountCalendars.All(c => c.IsChecked))
            {
                IsCheckedState = true;
            }
            else if (AccountCalendars.All(c => !c.IsChecked))
            {
                IsCheckedState = false;
            }
            else
            {
                IsCheckedState = null;
            }

            _isExternalPropChangeBlocked = false;
        }

        partial void OnIsCheckedStateChanged(bool? newValue)
        {
            if (_isExternalPropChangeBlocked) return;

            if (newValue == null)
            {
                // Only primary calendars must be checked.

                foreach (var calendar in AccountCalendars)
                {
                    calendar.IsChecked = calendar.IsPrimary;
                }
            }
            else
            {
                foreach (var calendar in AccountCalendars)
                {
                    calendar.IsChecked = newValue.GetValueOrDefault();
                }
            }
        }
    }
}
