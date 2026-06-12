using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Domain.MenuItems;

public partial class MergedAccountMenuItem : MenuItemBase<MergedInbox, IMenuItem>, IMergedAccountMenuItem
{
    public int MergedAccountCount => HoldingAccounts?.Count() ?? 0;

    public IEnumerable<MailAccount> HoldingAccounts { get; }

    [ObservableProperty]
    private int unreadItemCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSynchronizationProgressVisible), nameof(IsProgressIndeterminate), nameof(SynchronizationProgress), nameof(SynchronizationProgressValue))]
    public partial bool IsSynchronizationInProgress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SynchronizationProgress), nameof(SynchronizationProgressValue), nameof(IsProgressIndeterminate))]
    public partial int TotalItemsToSync { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SynchronizationProgress), nameof(SynchronizationProgressValue), nameof(IsProgressIndeterminate))]
    public partial int RemainingItemsToSync { get; set; }

    [ObservableProperty]
    public partial string SynchronizationStatus { get; set; } = string.Empty;

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

    public bool IsSynchronizationProgressVisible => IsSynchronizationInProgress;

    public bool IsProgressIndeterminate => IsSynchronizationInProgress && TotalItemsToSync <= 0;

    [ObservableProperty]
    private string mergedAccountName;

    [ObservableProperty]
    private bool _isEnabled = true;

    public MergedAccountMenuItem(MergedInbox mergedInbox, IEnumerable<MailAccount> holdingAccounts, IMenuItem parent) : base(mergedInbox, mergedInbox.Id, parent)
    {
        MergedAccountName = mergedInbox.Name;
        HoldingAccounts = holdingAccounts;
    }

    public void RefreshFolderItemCount()
    {
        UnreadItemCount = SubMenuItems.OfType<IAccountMenuItem>().Sum(a => a.UnreadItemCount);
    }

    public void RefreshSynchronizationProgress()
    {
        var activeAccountMenuItems = SubMenuItems
            .OfType<IAccountMenuItem>()
            .Where(a => a.IsSynchronizationInProgress)
            .ToList();

        IsSynchronizationInProgress = activeAccountMenuItems.Any();
        TotalItemsToSync = activeAccountMenuItems.Sum(a => a.TotalItemsToSync);
        RemainingItemsToSync = activeAccountMenuItems.Sum(a => a.RemainingItemsToSync);
        SynchronizationStatus = activeAccountMenuItems
            .Select(a => a.SynchronizationStatus)
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? string.Empty;
    }

    public void ApplySynchronizationProgress(AccountSynchronizationProgress progress)
    {
        if (progress == null)
            return;

        IsSynchronizationInProgress = progress.IsInProgress;
        TotalItemsToSync = progress.TotalUnits;
        RemainingItemsToSync = progress.RemainingUnits;
        SynchronizationStatus = progress.Status ?? string.Empty;
    }

    public void UpdateAccount(MailAccount account)
    {
    }
}
