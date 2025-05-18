using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Core.Domain.Entities.Shared;

namespace Wino.Calendar.Services;

/// <summary>
/// Encapsulated state manager for collectively managing the state of account calendars.
/// Callers must react to the events to update their state only from this service.
/// </summary>
public partial class AccountCalendarStateService : ObservableObject, IAccountCalendarStateService
{
    public event EventHandler<GroupedAccountCalendarViewModel> CollectiveAccountGroupSelectionStateChanged;
    public event EventHandler<AccountCalendarViewModel> AccountCalendarSelectionStateChanged;

    [ObservableProperty]
    public partial ReadOnlyObservableCollection<GroupedAccountCalendarViewModel> GroupedAccountCalendars { get; set; }

    private ObservableCollection<GroupedAccountCalendarViewModel> _internalGroupedAccountCalendars = new ObservableCollection<GroupedAccountCalendarViewModel>();

    public IEnumerable<AccountCalendarViewModel> ActiveCalendars
    {
        get
        {
            return GroupedAccountCalendars
            .SelectMany(a => a.AccountCalendars)
            .Where(b => b.IsChecked);
        }
    }

    public IEnumerable<IGrouping<MailAccount, AccountCalendarViewModel>> GroupedAccountCalendarsEnumerable
    {
        get
        {
            return GroupedAccountCalendars
            .Select(a => a.AccountCalendars)
            .SelectMany(b => b)
            .GroupBy(c => c.Account);
        }
    }

    public AccountCalendarStateService()
    {
        GroupedAccountCalendars = new ReadOnlyObservableCollection<GroupedAccountCalendarViewModel>(_internalGroupedAccountCalendars);
    }

    private void SingleGroupCalendarCollectiveStateChanged(object sender, EventArgs e)
        => CollectiveAccountGroupSelectionStateChanged?.Invoke(this, sender as GroupedAccountCalendarViewModel);

    private void SingleCalendarSelectionStateChanged(object sender, AccountCalendarViewModel e)
        => AccountCalendarSelectionStateChanged?.Invoke(this, e);

    public void AddGroupedAccountCalendar(GroupedAccountCalendarViewModel groupedAccountCalendar)
    {
        groupedAccountCalendar.CalendarSelectionStateChanged += SingleCalendarSelectionStateChanged;
        groupedAccountCalendar.CollectiveSelectionStateChanged += SingleGroupCalendarCollectiveStateChanged;

        _internalGroupedAccountCalendars.Add(groupedAccountCalendar);
    }

    public void RemoveGroupedAccountCalendar(GroupedAccountCalendarViewModel groupedAccountCalendar)
    {
        groupedAccountCalendar.CalendarSelectionStateChanged -= SingleCalendarSelectionStateChanged;
        groupedAccountCalendar.CollectiveSelectionStateChanged -= SingleGroupCalendarCollectiveStateChanged;

        _internalGroupedAccountCalendars.Remove(groupedAccountCalendar);
    }

    public void ClearGroupedAccountCalendar()
    {
        foreach (var groupedAccountCalendar in _internalGroupedAccountCalendars)
        {
            RemoveGroupedAccountCalendar(groupedAccountCalendar);
        }
    }

    public void AddAccountCalendar(AccountCalendarViewModel accountCalendar)
    {
        // Find the group that this calendar belongs to.
        var group = _internalGroupedAccountCalendars.FirstOrDefault(g => g.Account.Id == accountCalendar.Account.Id);

        if (group == null)
        {
            // If the group doesn't exist, create it.
            group = new GroupedAccountCalendarViewModel(accountCalendar.Account, new[] { accountCalendar });
            AddGroupedAccountCalendar(group);
        }
        else
        {
            group.AccountCalendars.Add(accountCalendar);
        }
    }

    public void RemoveAccountCalendar(AccountCalendarViewModel accountCalendar)
    {
        var group = _internalGroupedAccountCalendars.FirstOrDefault(g => g.Account.Id == accountCalendar.Account.Id);

        // We don't expect but just in case.
        if (group == null) return;

        group.AccountCalendars.Remove(accountCalendar);

        if (group.AccountCalendars.Count == 0)
        {
            RemoveGroupedAccountCalendar(group);
        }
    }
}
