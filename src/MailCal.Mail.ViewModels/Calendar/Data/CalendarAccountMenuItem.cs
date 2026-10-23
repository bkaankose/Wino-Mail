using System.ComponentModel;
using System.Threading.Tasks;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Extensions;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.MenuItems;
using MailCal.Core.Domain.Models.Navigation;

namespace MailCal.Calendar.ViewModels.Data;

/// <summary>
/// Account selector used by the ungrouped calendar shell projection.
/// </summary>
public sealed partial class CalendarAccountMenuItem : MenuItemBase<GroupedAccountCalendarViewModel>, IAccountNavigationMenuItem
{
    private readonly System.Func<System.Guid, Task> _synchronizeAccount;

    public CalendarAccountMenuItem(GroupedAccountCalendarViewModel group, System.Func<System.Guid, Task> synchronizeAccount)
        : base(group, group.Account?.Id)
    {
        _synchronizeAccount = synchronizeAccount;
        Parameter.PropertyChanged += GroupPropertyChanged;
    }

    public MailAccount Account => Parameter.Account;
    public string AccountName => Account?.Name ?? string.Empty;
    public string AccountAddress => Account?.Address ?? string.Empty;
    public int UnreadItemCount => 0;
    public bool IsSynchronizationProgressVisible => Parameter.IsSynchronizationProgressVisible;
    public bool IsProgressIndeterminate => Parameter.IsProgressIndeterminate;
    public double SynchronizationProgressValue => Parameter.SynchronizationProgressValue;
    public bool IsAttentionRequired => Account.RequiresAttention(MailCalApplicationMode.Calendar);
    public bool SupportsMailAccountActions => false;
    public AccountDetailsTab AccountDetailsTab => global::MailCal.Core.Domain.Models.Navigation.AccountDetailsTab.Calendar;
    public bool SupportsAccountSynchronization => !IsAttentionRequired;
    public bool SelectsOnInvoked => true;

    /// <summary>A sign-in that is still pending can only fail, so the row offers Fix instead.</summary>
    public Task SynchronizeAccountAsync()
        => SupportsAccountSynchronization ? _synchronizeAccount(Account.Id) : Task.CompletedTask;

    public void UpdateGroup(GroupedAccountCalendarViewModel group)
    {
        if (ReferenceEquals(Parameter, group))
            return;

        Parameter.PropertyChanged -= GroupPropertyChanged;
        Parameter = group;
        Parameter.PropertyChanged += GroupPropertyChanged;
        NotifyAccountPropertiesChanged();
    }

    public void Detach() => Parameter.PropertyChanged -= GroupPropertyChanged;

    private void GroupPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GroupedAccountCalendarViewModel.Account)
            or nameof(GroupedAccountCalendarViewModel.AccountAddressDisplay))
        {
            NotifyAccountPropertiesChanged();
        }

        if (e.PropertyName is nameof(GroupedAccountCalendarViewModel.IsSynchronizationProgressVisible)
            or nameof(GroupedAccountCalendarViewModel.IsProgressIndeterminate)
            or nameof(GroupedAccountCalendarViewModel.SynchronizationProgressValue))
        {
            OnPropertyChanged(e.PropertyName);
        }
    }

    private void NotifyAccountPropertiesChanged()
    {
        OnPropertyChanged(nameof(Account));
        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(AccountAddress));
        OnPropertyChanged(nameof(IsAttentionRequired));
        OnPropertyChanged(nameof(SupportsAccountSynchronization));
    }
}
