using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Collections;
using Wino.Calendar.ViewModels.Data;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Calendar.ViewModels.Interfaces;

public interface IAccountCalendarStateService : INotifyPropertyChanged
{
    IDispatcher Dispatcher { get; set; }
    ReadOnlyObservableCollection<GroupedAccountCalendarViewModel> GroupedAccountCalendars { get; }

    event EventHandler<GroupedAccountCalendarViewModel> CollectiveAccountGroupSelectionStateChanged;
    event EventHandler<AccountCalendarViewModel> AccountCalendarSelectionStateChanged;

    public void AddGroupedAccountCalendar(GroupedAccountCalendarViewModel groupedAccountCalendar);
    public void RemoveGroupedAccountCalendar(GroupedAccountCalendarViewModel groupedAccountCalendar);
    public void ClearGroupedAccountCalendars();

    public void AddAccountCalendar(AccountCalendarViewModel accountCalendar);
    public void RemoveAccountCalendar(AccountCalendarViewModel accountCalendar);

    /// <summary>
    /// Enumeration of currently selected calendars.
    /// </summary>
    IEnumerable<AccountCalendarViewModel> ActiveCalendars { get; }
    IEnumerable<AccountCalendarViewModel> AllCalendars { get; }
    ReadOnlyObservableGroupedCollection<MailAccount, AccountCalendarViewModel> GroupedCalendars { get; set; }
}
