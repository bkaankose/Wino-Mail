using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Calendar.ViewModels.Interfaces
{
    public interface IAccountCalendarStateService : INotifyPropertyChanged
    {
        ReadOnlyObservableCollection<GroupedAccountCalendarViewModel> GroupedAccountCalendars { get; }

        event EventHandler<GroupedAccountCalendarViewModel> CollectiveAccountGroupSelectionStateChanged;
        event EventHandler<AccountCalendarViewModel> AccountCalendarSelectionStateChanged;

        public void AddGroupedAccountCalendar(GroupedAccountCalendarViewModel groupedAccountCalendar);
        public void RemoveGroupedAccountCalendar(GroupedAccountCalendarViewModel groupedAccountCalendar);
        public void ClearGroupedAccountCalendar();

        public void AddAccountCalendar(AccountCalendarViewModel accountCalendar);
        public void RemoveAccountCalendar(AccountCalendarViewModel accountCalendar);

        ObservableCollection<CalendarItemViewModel> SelectedItems { get; }
        bool HasMultipleSelectedItems { get; }

        /// <summary>
        /// Enumeration of currently selected calendars.
        /// </summary>
        IEnumerable<AccountCalendarViewModel> ActiveCalendars { get; }
        IEnumerable<IGrouping<MailAccount, AccountCalendarViewModel>> GroupedAccountCalendarsEnumerable { get; }
    }
}
