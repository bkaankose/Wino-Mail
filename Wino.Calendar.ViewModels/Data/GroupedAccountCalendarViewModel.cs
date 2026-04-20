using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Calendar.ViewModels.Data;

public partial class GroupedAccountCalendarViewModel : ObservableObject
{
    public event EventHandler CollectiveSelectionStateChanged;
    public event EventHandler<AccountCalendarViewModel> CalendarSelectionStateChanged;

    public MailAccount Account { get; }
    public ObservableCollection<AccountCalendarViewModel> AccountCalendars { get; }

    public static bool SupportsCalendar(MailAccount account)
        => account?.IsCalendarAccessGranted == true;

    public GroupedAccountCalendarViewModel(MailAccount account, IEnumerable<AccountCalendarViewModel> calendarViewModels)
    {
        Account = account;
        AccountCalendars = new ObservableCollection<AccountCalendarViewModel>(calendarViewModels);
        AccountColorHex = account.AccountColorHex;

        ManageIsCheckedState();

        foreach (var calendarViewModel in calendarViewModels)
        {
            calendarViewModel.PropertyChanged += CalendarPropertyChanged;
        }

        AccountCalendars.CollectionChanged += CalendarListUpdated;
    }

    private void CalendarListUpdated(object sender, NotifyCollectionChangedEventArgs e)
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
        if (sender is AccountCalendarViewModel viewModel &&
            e.PropertyName == nameof(AccountCalendarViewModel.IsChecked))
        {
            ManageIsCheckedState();
            UpdateCalendarCheckedState(viewModel, viewModel.IsChecked, true);
        }
    }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    [ObservableProperty]
    public partial bool? IsCheckedState { get; set; } = true;

    [ObservableProperty]
    public partial string AccountColorHex { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSynchronize), nameof(IsSynchronizationProgressVisible), nameof(IsProgressIndeterminate))]
    public partial bool IsSynchronizationInProgress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SynchronizationProgress), nameof(SynchronizationProgressValue), nameof(IsProgressIndeterminate))]
    public partial int TotalItemsToSync { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SynchronizationProgress), nameof(SynchronizationProgressValue), nameof(IsProgressIndeterminate))]
    public partial int RemainingItemsToSync { get; set; }

    [ObservableProperty]
    public partial string SynchronizationStatus { get; set; } = string.Empty;

    public bool CanSynchronize => !IsSynchronizationInProgress;
    public bool IsSynchronizationProgressVisible => IsSynchronizationInProgress;
    public bool IsProgressIndeterminate => IsSynchronizationInProgress && TotalItemsToSync <= 0;
    public string AccountAddressDisplay => string.IsNullOrWhiteSpace(Account?.Address) ? string.Empty : $" ({Account.Address})";

    public double SynchronizationProgress
    {
        get
        {
            if (TotalItemsToSync <= 0)
                return 0;

            return ((double)(TotalItemsToSync - RemainingItemsToSync) / TotalItemsToSync) * 100;
        }
    }

    public double SynchronizationProgressValue => SynchronizationProgress;

    private bool _isExternalPropChangeBlocked;

    public void ApplySynchronizationProgress(AccountSynchronizationProgress progress)
    {
        if (progress == null || progress.AccountId != Account.Id)
            return;

        IsSynchronizationInProgress = progress.IsInProgress;
        TotalItemsToSync = progress.TotalUnits;
        RemainingItemsToSync = progress.RemainingUnits;
        SynchronizationStatus = progress.Status ?? string.Empty;
    }

    private void ManageIsCheckedState()
    {
        if (_isExternalPropChangeBlocked)
            return;

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

    partial void OnIsCheckedStateChanged(bool? oldValue, bool? newValue)
    {
        if (_isExternalPropChangeBlocked)
            return;

        _isExternalPropChangeBlocked = true;

        if (newValue == null)
        {
            foreach (var calendar in AccountCalendars)
            {
                UpdateCalendarCheckedState(calendar, calendar.IsPrimary);
            }
        }
        else
        {
            foreach (var calendar in AccountCalendars)
            {
                UpdateCalendarCheckedState(calendar, newValue.GetValueOrDefault());
            }
        }

        _isExternalPropChangeBlocked = false;
        CollectiveSelectionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCalendarCheckedState(AccountCalendarViewModel accountCalendarViewModel, bool newValue, bool ignoreValueCheck = false)
    {
        var currentValue = accountCalendarViewModel.IsChecked;

        if (currentValue == newValue && !ignoreValueCheck)
            return;

        accountCalendarViewModel.IsChecked = newValue;

        if (_isExternalPropChangeBlocked)
            return;

        CalendarSelectionStateChanged?.Invoke(this, accountCalendarViewModel);
    }

    public void UpdateAccount(MailAccount updatedAccount)
    {
        if (updatedAccount == null || updatedAccount.Id != Account.Id)
            return;

        Account.Name = updatedAccount.Name;
        Account.Address = updatedAccount.Address;
        Account.AccountColorHex = updatedAccount.AccountColorHex;
        Account.AttentionReason = updatedAccount.AttentionReason;
        Account.MergedInboxId = updatedAccount.MergedInboxId;
        AccountColorHex = updatedAccount.AccountColorHex;
        OnPropertyChanged(nameof(Account));
        OnPropertyChanged(nameof(AccountAddressDisplay));
    }
}
