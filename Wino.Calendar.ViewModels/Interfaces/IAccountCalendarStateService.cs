using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Wino.Calendar.ViewModels.Data;

namespace Wino.Calendar.ViewModels.Interfaces
{
    public interface IAccountCalendarStateService
    {
        ReadOnlyObservableCollection<GroupedAccountCalendarViewModel> GroupedAccountCalendars { get; }

        event EventHandler<GroupedAccountCalendarViewModel> CollectiveAccountGroupSelectionStateChanged;
        event EventHandler<AccountCalendarViewModel> AccountCalendarSelectionStateChanged;

        public void AddGroupedAccountCalendar(GroupedAccountCalendarViewModel groupedAccountCalendar);
        public void RemoveGroupedAccountCalendar(GroupedAccountCalendarViewModel groupedAccountCalendar);
        public void ClearGroupedAccountCalendar();

        public void AddAccountCalendar(AccountCalendarViewModel accountCalendar);
        public void RemoveAccountCalendar(AccountCalendarViewModel accountCalendar);

        /// <summary>
        /// Enumeration of currently selected calendars.
        /// </summary>
        IEnumerable<AccountCalendarViewModel> ActiveCalendars { get; }
    }
}
