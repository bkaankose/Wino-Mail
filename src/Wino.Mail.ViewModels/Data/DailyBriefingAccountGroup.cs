#nullable enable
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Wino.Core.Domain.Models.Intelligence;

namespace Wino.Mail.ViewModels;

public sealed class DailyBriefingAccountGroup : ObservableCollection<DailyBriefingItem>
{
    public DailyBriefingAccountGroup(DailyBriefingAccount account, IEnumerable<DailyBriefingItem> items)
        : base(items)
    {
        Account = account;
    }

    public DailyBriefingAccount Account { get; }

    public string AccountName => Account.Account.Name;

    public string AccountInitials => DailyBriefingPanelViewModel.GetInitials(AccountName);
}
