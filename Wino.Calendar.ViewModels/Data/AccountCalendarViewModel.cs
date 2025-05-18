using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Calendar;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Calendar.ViewModels.Data;

public partial class AccountCalendarViewModel : ObservableObject, IAccountCalendar
{
    public MailAccount Account { get; }
    public AccountCalendar AccountCalendar { get; }

    public AccountCalendarViewModel(MailAccount account, AccountCalendar accountCalendar)
    {
        Account = account;
        AccountCalendar = accountCalendar;

        IsChecked = accountCalendar.IsExtended;
    }

    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => IsExtended = value;

    public string Name
    {
        get => AccountCalendar.Name;
        set => SetProperty(AccountCalendar.Name, value, AccountCalendar, (u, n) => u.Name = n);
    }

    public string TextColorHex
    {
        get => AccountCalendar.TextColorHex;
        set => SetProperty(AccountCalendar.TextColorHex, value, AccountCalendar, (u, t) => u.TextColorHex = t);
    }

    public string BackgroundColorHex
    {
        get => AccountCalendar.BackgroundColorHex;
        set => SetProperty(AccountCalendar.BackgroundColorHex, value, AccountCalendar, (u, b) => u.BackgroundColorHex = b);
    }

    public bool IsExtended
    {
        get => AccountCalendar.IsExtended;
        set => SetProperty(AccountCalendar.IsExtended, value, AccountCalendar, (u, i) => u.IsExtended = i);
    }

    public bool IsPrimary
    {
        get => AccountCalendar.IsPrimary;
        set => SetProperty(AccountCalendar.IsPrimary, value, AccountCalendar, (u, i) => u.IsPrimary = i);
    }

    public Guid AccountId
    {
        get => AccountCalendar.AccountId;
        set => SetProperty(AccountCalendar.AccountId, value, AccountCalendar, (u, a) => u.AccountId = a);
    }

    public string RemoteCalendarId
    {
        get => AccountCalendar.RemoteCalendarId;
        set => SetProperty(AccountCalendar.RemoteCalendarId, value, AccountCalendar, (u, r) => u.RemoteCalendarId = r);
    }
    public Guid Id { get => ((IAccountCalendar)AccountCalendar).Id; set => ((IAccountCalendar)AccountCalendar).Id = value; }
}
