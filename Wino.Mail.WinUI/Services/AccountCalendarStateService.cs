using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Wino.Calendar.ViewModels.Data;
using Wino.Calendar.ViewModels.Interfaces;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Messaging.Client.Calendar;

namespace Wino.Mail.WinUI.Services;

/// <summary>
/// Encapsulated state manager for collectively managing the state of account calendars.
/// Callers must react to the events to update their state only from this service.
/// </summary>
public partial class AccountCalendarStateService : ObservableRecipient,
    IAccountCalendarStateService,
    IRecipient<CalendarListAdded>,
    IRecipient<CalendarListUpdated>,
    IRecipient<CalendarListDeleted>
{
    public IDispatcher? Dispatcher { get; set; }

    public event EventHandler<GroupedAccountCalendarViewModel>? CollectiveAccountGroupSelectionStateChanged;
    public event EventHandler<AccountCalendarViewModel>? AccountCalendarSelectionStateChanged;

    private readonly ObservableCollection<GroupedAccountCalendarViewModel> _internalGroupedAccountCalendars;
    private readonly ObservableGroupedCollection<MailAccount, AccountCalendarViewModel> _internalGroupedCalendars;

    [ObservableProperty]
    public partial ReadOnlyObservableCollection<GroupedAccountCalendarViewModel> GroupedAccountCalendars { get; set; }

    [ObservableProperty]
    public partial ReadOnlyObservableGroupedCollection<MailAccount, AccountCalendarViewModel> GroupedCalendars { get; set; }

    public IEnumerable<AccountCalendarViewModel> ActiveCalendars
    {
        get
        {
            return _internalGroupedAccountCalendars
                .SelectMany(a => a.AccountCalendars)
                .Where(b => b.IsChecked);
        }
    }

    public IEnumerable<AccountCalendarViewModel> AllCalendars
    {
        get
        {
            return _internalGroupedAccountCalendars
                .SelectMany(a => a.AccountCalendars);
        }
    }

    private readonly IAccountService _accountService;

    public AccountCalendarStateService(IAccountService accountService)
    {
        _accountService = accountService;

        _internalGroupedAccountCalendars = new ObservableCollection<GroupedAccountCalendarViewModel>();
        GroupedAccountCalendars = new ReadOnlyObservableCollection<GroupedAccountCalendarViewModel>(_internalGroupedAccountCalendars);

        _internalGroupedCalendars = new ObservableGroupedCollection<MailAccount, AccountCalendarViewModel>();
        GroupedCalendars = new ReadOnlyObservableGroupedCollection<MailAccount, AccountCalendarViewModel>(_internalGroupedCalendars);

        Messenger.Register<CalendarListAdded>(this);
        Messenger.Register<CalendarListUpdated>(this);
        Messenger.Register<CalendarListDeleted>(this);
    }

    private void SingleGroupCalendarCollectiveStateChanged(object? sender, EventArgs e)
        => CollectiveAccountGroupSelectionStateChanged?.Invoke(this, sender as GroupedAccountCalendarViewModel ?? throw new InvalidOperationException("Sender must be GroupedAccountCalendarViewModel"));

    private void SingleCalendarSelectionStateChanged(object? sender, AccountCalendarViewModel e)
        => AccountCalendarSelectionStateChanged?.Invoke(this, e);

    public void AddGroupedAccountCalendar(GroupedAccountCalendarViewModel groupedAccountCalendar)
    {
        groupedAccountCalendar.CalendarSelectionStateChanged += SingleCalendarSelectionStateChanged;
        groupedAccountCalendar.CollectiveSelectionStateChanged += SingleGroupCalendarCollectiveStateChanged;

        _internalGroupedAccountCalendars.Add(groupedAccountCalendar);

        // Maintain the grouped calendars collection
        var group = _internalGroupedCalendars.FirstOrDefault<ObservableGroup<MailAccount, AccountCalendarViewModel>>(g => g.Key.Id == groupedAccountCalendar.Account.Id);
        if (group == null)
        {
            _internalGroupedCalendars.Add(new ObservableGroup<MailAccount, AccountCalendarViewModel>(groupedAccountCalendar.Account, groupedAccountCalendar.AccountCalendars));
        }
        else
        {
            foreach (var calendar in groupedAccountCalendar.AccountCalendars)
            {
                group.Add(calendar);
            }
        }
    }

    public void RemoveGroupedAccountCalendar(GroupedAccountCalendarViewModel groupedAccountCalendar)
    {
        groupedAccountCalendar.CalendarSelectionStateChanged -= SingleCalendarSelectionStateChanged;
        groupedAccountCalendar.CollectiveSelectionStateChanged -= SingleGroupCalendarCollectiveStateChanged;

        _internalGroupedAccountCalendars.Remove(groupedAccountCalendar);

        // Maintain the grouped calendars collection
        var group = _internalGroupedCalendars.FirstOrDefault<ObservableGroup<MailAccount, AccountCalendarViewModel>>(g => g.Key.Id == groupedAccountCalendar.Account.Id);
        if (group != null)
        {
            foreach (var calendar in groupedAccountCalendar.AccountCalendars.ToList())
            {
                group.Remove(calendar);
            }

            if (group.Count == 0)
            {
                _internalGroupedCalendars.Remove(group);
            }
        }
    }

    public void ClearGroupedAccountCalendars()
    {
        while (_internalGroupedAccountCalendars.Any())
        {
            RemoveGroupedAccountCalendar(_internalGroupedAccountCalendars[0]);
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

            // Maintain the grouped calendars collection
            var calendarGroup = _internalGroupedCalendars.FirstOrDefault<ObservableGroup<MailAccount, AccountCalendarViewModel>>(g => g.Key.Id == accountCalendar.Account.Id);
            if (calendarGroup == null)
            {
                _internalGroupedCalendars.Add(new ObservableGroup<MailAccount, AccountCalendarViewModel>(accountCalendar.Account, new[] { accountCalendar }));
            }
            else
            {
                calendarGroup.Add(accountCalendar);
            }
        }
    }

    public void RemoveAccountCalendar(AccountCalendarViewModel accountCalendar)
    {
        var group = _internalGroupedAccountCalendars.FirstOrDefault(g => g.Account.Id == accountCalendar.Account.Id);

        // We don't expect but just in case.
        if (group == null) return;

        group.AccountCalendars.Remove(accountCalendar);

        // Maintain the grouped calendars collection
        var calendarGroup = _internalGroupedCalendars.FirstOrDefault<ObservableGroup<MailAccount, AccountCalendarViewModel>>(g => g.Key.Id == accountCalendar.Account.Id);
        if (calendarGroup != null)
        {
            calendarGroup.Remove(accountCalendar);

            if (calendarGroup.Count == 0)
            {
                _internalGroupedCalendars.Remove(calendarGroup);
            }
        }

        if (group.AccountCalendars.Count == 0)
        {
            RemoveGroupedAccountCalendar(group);
        }
    }

    public async void Receive(CalendarListAdded message)
    {
        var accountCalendar = message.AccountCalendar;
        var mailAccount = await _accountService.GetAccountAsync(accountCalendar.AccountId);

        if (mailAccount == null) return;

        var accountCalendarViewModel = new AccountCalendarViewModel(mailAccount, accountCalendar);
        
        if (Dispatcher != null)
        {
            await Dispatcher.ExecuteOnUIThread(() => AddAccountCalendar(accountCalendarViewModel));
        }
        else
        {
            AddAccountCalendar(accountCalendarViewModel);
        }
    }

    public async void Receive(CalendarListUpdated message)
    {
        var accountCalendar = message.AccountCalendar;

        if (Dispatcher != null)
        {
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                // Find the existing calendar view model
                var existingCalendar = AllCalendars.FirstOrDefault(c => c.Id == accountCalendar.Id);

                if (existingCalendar != null)
                {
                    // Update properties
                    existingCalendar.Name = accountCalendar.Name;
                    existingCalendar.TextColorHex = accountCalendar.TextColorHex;
                    existingCalendar.BackgroundColorHex = accountCalendar.BackgroundColorHex;
                    existingCalendar.IsExtended = accountCalendar.IsExtended;
                    existingCalendar.IsPrimary = accountCalendar.IsPrimary;
                }
            });
        }
        else
        {
            // Find the existing calendar view model
            var existingCalendar = AllCalendars.FirstOrDefault(c => c.Id == accountCalendar.Id);

            if (existingCalendar != null)
            {
                // Update properties
                existingCalendar.Name = accountCalendar.Name;
                existingCalendar.TextColorHex = accountCalendar.TextColorHex;
                existingCalendar.BackgroundColorHex = accountCalendar.BackgroundColorHex;
                existingCalendar.IsExtended = accountCalendar.IsExtended;
                existingCalendar.IsPrimary = accountCalendar.IsPrimary;
            }
        }
    }

    public async void Receive(CalendarListDeleted message)
    {
        var accountCalendar = message.AccountCalendar;

        if (Dispatcher != null)
        {
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                // Find and remove the calendar view model
                var existingCalendar = AllCalendars.FirstOrDefault(c => c.Id == accountCalendar.Id);

                if (existingCalendar != null)
                {
                    RemoveAccountCalendar(existingCalendar);
                }
            });
        }
        else
        {
            // Find and remove the calendar view model
            var existingCalendar = AllCalendars.FirstOrDefault(c => c.Id == accountCalendar.Id);

            if (existingCalendar != null)
            {
                RemoveAccountCalendar(existingCalendar);
            }
        }
    }
}
