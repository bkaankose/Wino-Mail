using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.MenuItems;

public partial class MergedAccountMenuItem : MenuItemBase<MergedInbox, IMenuItem>, IMergedAccountMenuItem
{
    public int MergedAccountCount => HoldingAccounts?.Count() ?? 0;

    public IEnumerable<MailAccount> HoldingAccounts { get; }

    [ObservableProperty]
    private int unreadItemCount;

    /// <summary>
    /// Total items to sync across all merged accounts.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SynchronizationProgress))]
    public partial int TotalItemsToSync { get; set; }

    /// <summary>
    /// Remaining items to sync across all merged accounts.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SynchronizationProgress))]
    public partial int RemainingItemsToSync { get; set; }

    /// <summary>
    /// Current synchronization status message.
    /// </summary>
    [ObservableProperty]
    public partial string SynchronizationStatus { get; set; } = string.Empty;

    /// <summary>
    /// Calculated synchronization progress for merged accounts.
    /// </summary>
    public double SynchronizationProgress
    {
        get
        {
            if (TotalItemsToSync == 0 || RemainingItemsToSync == 0)
                return -1; // Indeterminate

            return ((double)(TotalItemsToSync - RemainingItemsToSync) / TotalItemsToSync) * 100;
        }
    }

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
    
    /// <summary>
    /// Aggregates synchronization progress from all child account menu items.
    /// </summary>
    public void RefreshSynchronizationProgress()
    {
        var accountMenuItems = SubMenuItems.OfType<IAccountMenuItem>().ToList();
        
        TotalItemsToSync = accountMenuItems.Sum(a => a.TotalItemsToSync);
        RemainingItemsToSync = accountMenuItems.Sum(a => a.RemainingItemsToSync);
        
        // Use first non-empty status message
        SynchronizationStatus = accountMenuItems.FirstOrDefault(a => !string.IsNullOrEmpty(a.SynchronizationStatus))?.SynchronizationStatus ?? string.Empty;
    }

    public void UpdateAccount(MailAccount account)
    {

    }
}
