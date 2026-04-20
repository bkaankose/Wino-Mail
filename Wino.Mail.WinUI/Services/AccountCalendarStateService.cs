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
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Services;
using Wino.Messaging.Client.Calendar;
using Wino.Messaging.UI;

namespace Wino.Mail.WinUI.Services;

/// <summary>
/// Encapsulated state manager for collectively managing the state of account calendars.
/// Callers must react to the events to update their state only from this service.
/// </summary>
public partial class AccountCalendarStateService : ObservableRecipient,
    IAccountCalendarStateService,
    IRecipient<CalendarListAdded>,
    IRecipient<CalendarListUpdated>,
    IRecipient<CalendarListDeleted>,
    IRecipient<AccountRemovedMessage>,
    IRecipient<AccountUpdatedMessage>,
    IRecipient<AccountSynchronizationProgressUpdatedMessage>
{
    private readonly object _calendarStateLock = new();

    public IDispatcher? Dispatcher { get; set; }

    public event EventHandler<GroupedAccountCalendarViewModel>? CollectiveAccountGroupSelectionStateChanged;
    public event EventHandler<AccountCalendarViewModel>? AccountCalendarSelectionStateChanged;

    private readonly ObservableCollection<GroupedAccountCalendarViewModel> _internalGroupedAccountCalendars;
    private readonly ObservableGroupedCollection<MailAccount, AccountCalendarViewModel> _internalGroupedCalendars;

    [ObservableProperty]
    public partial ReadOnlyObservableCollection<GroupedAccountCalendarViewModel> GroupedAccountCalendars { get; set; }

    [ObservableProperty]
    public partial ReadOnlyObservableGroupedCollection<MailAccount, AccountCalendarViewModel> GroupedCalendars { get; set; }

    [ObservableProperty]
    public partial bool IsAnySynchronizationInProgress { get; set; }

    public IEnumerable<AccountCalendarViewModel> ActiveCalendars
    {
        get
        {
            lock (_calendarStateLock)
            {
                return _internalGroupedAccountCalendars
                    .SelectMany(a => a.AccountCalendars)
                    .Where(b => b.IsChecked)
                    .ToList();
            }
        }
    }

    public IEnumerable<AccountCalendarViewModel> AllCalendars
    {
        get
        {
            lock (_calendarStateLock)
            {
                return _internalGroupedAccountCalendars
                    .SelectMany(a => a.AccountCalendars)
                    .ToList();
            }
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
        Messenger.Register<AccountRemovedMessage>(this);
        Messenger.Register<AccountUpdatedMessage>(this);
        Messenger.Register<AccountSynchronizationProgressUpdatedMessage>(this);
    }

    private void SingleGroupCalendarCollectiveStateChanged(object? sender, EventArgs e)
        => CollectiveAccountGroupSelectionStateChanged?.Invoke(this, sender as GroupedAccountCalendarViewModel ?? throw new InvalidOperationException("Sender must be GroupedAccountCalendarViewModel"));

    private void SingleCalendarSelectionStateChanged(object? sender, AccountCalendarViewModel e)
        => AccountCalendarSelectionStateChanged?.Invoke(this, e);

    public void AddGroupedAccountCalendar(GroupedAccountCalendarViewModel groupedAccountCalendar)
    {
        lock (_calendarStateLock)
        {
            if (!GroupedAccountCalendarViewModel.SupportsCalendar(groupedAccountCalendar.Account))
                return;

            groupedAccountCalendar.CalendarSelectionStateChanged += SingleCalendarSelectionStateChanged;
            groupedAccountCalendar.CollectiveSelectionStateChanged += SingleGroupCalendarCollectiveStateChanged;
            try
            {
                groupedAccountCalendar.ApplySynchronizationProgress(SynchronizationManager.Instance.GetSynchronizationProgress(
                    groupedAccountCalendar.Account.Id,
                    SynchronizationProgressCategory.Calendar));
            }
            catch (InvalidOperationException)
            {
            }

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

            UpdateAggregateSynchronizationState();
        }
    }

    public void RemoveGroupedAccountCalendar(GroupedAccountCalendarViewModel groupedAccountCalendar)
    {
        lock (_calendarStateLock)
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

            UpdateAggregateSynchronizationState();
        }
    }

    public void ClearGroupedAccountCalendars()
    {
        lock (_calendarStateLock)
        {
            while (_internalGroupedAccountCalendars.Any())
            {
                RemoveGroupedAccountCalendar(_internalGroupedAccountCalendars[0]);
            }
        }
    }

    public void AddAccountCalendar(AccountCalendarViewModel accountCalendar)
    {
        lock (_calendarStateLock)
        {
            if (!GroupedAccountCalendarViewModel.SupportsCalendar(accountCalendar.Account))
                return;

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
    }

    public void RemoveAccountCalendar(AccountCalendarViewModel accountCalendar)
    {
        lock (_calendarStateLock)
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
                    existingCalendar.IsSynchronizationEnabled = accountCalendar.IsSynchronizationEnabled;
                    existingCalendar.DefaultShowAs = accountCalendar.DefaultShowAs;
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
                existingCalendar.IsSynchronizationEnabled = accountCalendar.IsSynchronizationEnabled;
                existingCalendar.DefaultShowAs = accountCalendar.DefaultShowAs;
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

    public async void Receive(AccountRemovedMessage message)
    {
        var removedAccountId = message.Account.Id;

        if (Dispatcher != null)
        {
            await Dispatcher.ExecuteOnUIThread(() =>
            {
                GroupedAccountCalendarViewModel? groupedAccount;
                lock (_calendarStateLock)
                {
                    groupedAccount = _internalGroupedAccountCalendars.FirstOrDefault(a => a.Account.Id == removedAccountId);
                }

                if (groupedAccount != null)
                {
                    RemoveGroupedAccountCalendar(groupedAccount);
                }
            });
        }
        else
        {
            GroupedAccountCalendarViewModel? groupedAccount;
            lock (_calendarStateLock)
            {
                groupedAccount = _internalGroupedAccountCalendars.FirstOrDefault(a => a.Account.Id == removedAccountId);
            }

            if (groupedAccount != null)
            {
                RemoveGroupedAccountCalendar(groupedAccount);
            }
        }
    }

    public async void Receive(AccountUpdatedMessage message)
    {
        if (Dispatcher != null)
        {
            await Dispatcher.ExecuteOnUIThread(() => UpdateGroupedAccount(message.Account));
        }
        else
        {
            UpdateGroupedAccount(message.Account);
        }
    }

    public async void Receive(AccountSynchronizationProgressUpdatedMessage message)
    {
        if (message.Progress.Category != SynchronizationProgressCategory.Calendar)
            return;

        if (Dispatcher != null)
        {
            await Dispatcher.ExecuteOnUIThread(() => UpdateCalendarSynchronizationState(message.Progress));
        }
        else
        {
            UpdateCalendarSynchronizationState(message.Progress);
        }
    }

    private void UpdateGroupedAccount(MailAccount updatedAccount)
    {
        GroupedAccountCalendarViewModel? groupedAccount;
        lock (_calendarStateLock)
        {
            groupedAccount = _internalGroupedAccountCalendars.FirstOrDefault(a => a.Account.Id == updatedAccount.Id);

            if (!GroupedAccountCalendarViewModel.SupportsCalendar(updatedAccount))
            {
                if (groupedAccount != null)
                {
                    RemoveGroupedAccountCalendar(groupedAccount);
                }

                return;
            }
        }

        groupedAccount?.UpdateAccount(updatedAccount);
    }

    private void UpdateCalendarSynchronizationState(Wino.Core.Domain.Models.Synchronization.AccountSynchronizationProgress progress)
    {
        GroupedAccountCalendarViewModel? groupedAccount;
        lock (_calendarStateLock)
        {
            groupedAccount = _internalGroupedAccountCalendars.FirstOrDefault(a => a.Account.Id == progress.AccountId);
        }

        if (groupedAccount == null)
            return;

        groupedAccount.ApplySynchronizationProgress(progress);
        UpdateAggregateSynchronizationState();
    }

    private void UpdateAggregateSynchronizationState()
    {
        IsAnySynchronizationInProgress = _internalGroupedAccountCalendars.Any(a => a.IsSynchronizationInProgress);
    }
}
