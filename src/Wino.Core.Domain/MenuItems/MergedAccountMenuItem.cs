using System.Collections.Generic;
using System.Linq;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Accounts;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Domain.MenuItems;

public partial class MergedAccountMenuItem : MenuItemBase<MergedInbox, IMenuItem>, IMergedAccountMenuItem
{
    public int MergedAccountCount => HoldingAccounts?.Count() ?? 0;

    public IEnumerable<MailAccount> HoldingAccounts { get; }

    private int _unreadItemCount;
    private bool _isSynchronizationInProgress;
    private int _totalItemsToSync;
    private int _remainingItemsToSync;
    private string _synchronizationStatus = string.Empty;
    private string _mergedAccountName;

    public int UnreadItemCount
    {
        get => _unreadItemCount;
        set => SetProperty(ref _unreadItemCount, value);
    }

    public bool IsSynchronizationInProgress
    {
        get => _isSynchronizationInProgress;
        set
        {
            if (!SetProperty(ref _isSynchronizationInProgress, value))
                return;

            OnPropertyChanged(nameof(IsSynchronizationProgressVisible));
            OnPropertyChanged(nameof(IsProgressIndeterminate));
            OnPropertyChanged(nameof(SynchronizationProgress));
            OnPropertyChanged(nameof(SynchronizationProgressValue));
        }
    }

    public int TotalItemsToSync
    {
        get => _totalItemsToSync;
        set
        {
            if (!SetProperty(ref _totalItemsToSync, value))
                return;

            OnPropertyChanged(nameof(SynchronizationProgress));
            OnPropertyChanged(nameof(SynchronizationProgressValue));
            OnPropertyChanged(nameof(IsProgressIndeterminate));
        }
    }

    public int RemainingItemsToSync
    {
        get => _remainingItemsToSync;
        set
        {
            if (!SetProperty(ref _remainingItemsToSync, value))
                return;

            OnPropertyChanged(nameof(SynchronizationProgress));
            OnPropertyChanged(nameof(SynchronizationProgressValue));
            OnPropertyChanged(nameof(IsProgressIndeterminate));
        }
    }

    public string SynchronizationStatus
    {
        get => _synchronizationStatus;
        set => SetProperty(ref _synchronizationStatus, value);
    }

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

    public string MergedAccountName
    {
        get => _mergedAccountName;
        set => SetProperty(ref _mergedAccountName, value);
    }

    public string? ColorKey => AccountGroupColors.Normalize(Parameter?.ColorKey);

    public WinoIconGlyph IconGlyph => AccountGroupIcons.GetGlyph(Parameter?.Icon ?? AccountGroupIcon.PeopleLink);

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
