using System.ComponentModel;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.MenuItems;

namespace Wino.Calendar.ViewModels.Data;

/// <summary>
/// Account selector used by the ungrouped calendar shell projection.
/// </summary>
public sealed partial class CalendarAccountMenuItem : MenuItemBase<GroupedAccountCalendarViewModel>, IAccountNavigationMenuItem
{
    public CalendarAccountMenuItem(GroupedAccountCalendarViewModel group)
        : base(group, group.Account?.Id)
    {
        Parameter.PropertyChanged += GroupPropertyChanged;
    }

    public MailAccount Account => Parameter.Account;
    public string AccountName => Account?.Name ?? string.Empty;
    public string AccountAddress => Account?.Address ?? string.Empty;
    public int UnreadItemCount => 0;
    public bool IsSynchronizationProgressVisible => Parameter.IsSynchronizationProgressVisible;
    public bool IsProgressIndeterminate => Parameter.IsProgressIndeterminate;
    public double SynchronizationProgressValue => Parameter.SynchronizationProgressValue;
    public bool IsAttentionRequired => false;
    public bool SupportsMailAccountActions => false;
    public bool SelectsOnInvoked => true;

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
    }
}
