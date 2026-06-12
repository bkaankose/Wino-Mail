using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;
using Wino.Core.Domain.Models.Synchronization;

namespace Wino.Core.Domain.MenuItems;

public partial class AccountMenuItem : MenuItemBase<MailAccount, MenuItemBase<IMailItemFolder, FolderMenuItem>>, IAccountMenuItem
{
    [ObservableProperty]
    private int unreadItemCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSynchronizationProgressVisible), nameof(IsProgressIndeterminate), nameof(SynchronizationProgress), nameof(SynchronizationProgressValue))]
    public partial bool IsSynchronizationInProgress { get; set; }

    /// <summary>
    /// Total items to sync. 0 means indeterminate progress.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SynchronizationProgress), nameof(SynchronizationProgressValue), nameof(IsProgressIndeterminate))]
    public partial int TotalItemsToSync { get; set; }

    /// <summary>
    /// Remaining items to sync.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SynchronizationProgress), nameof(SynchronizationProgressValue), nameof(IsProgressIndeterminate))]
    public partial int RemainingItemsToSync { get; set; }

    /// <summary>
    /// Current synchronization status message.
    /// </summary>
    [ObservableProperty]
    public partial string SynchronizationStatus { get; set; } = string.Empty;

    [ObservableProperty]
    private bool _isEnabled = true;

    public bool IsAttentionRequired => AttentionReason != AccountAttentionReason.None;

    public double SynchronizationProgress
    {
        get
        {
            if (TotalItemsToSync <= 0)
                return 0;

            return Math.Clamp(((double)(TotalItemsToSync - RemainingItemsToSync) / TotalItemsToSync) * 100, 0, 100);
        }
    }

    public double SynchronizationProgressValue => SynchronizationProgress;

    public bool IsSynchronizationProgressVisible => IsSynchronizationInProgress;

    public bool IsProgressIndeterminate => IsSynchronizationInProgress && TotalItemsToSync <= 0;

    public Guid AccountId => Parameter.Id;

    private AccountAttentionReason attentionReason;

    public AccountAttentionReason AttentionReason
    {
        get => attentionReason;
        set
        {
            if (SetProperty(ref attentionReason, value))
            {
                OnPropertyChanged(nameof(IsAttentionRequired));
                UpdateFixAccountIssueMenuItem();
            }
        }
    }

    public string AccountName
    {
        get => Parameter.Name;
        set => SetProperty(Parameter.Name, value, Parameter, (u, n) => u.Name = n);
    }

    public string Base64ProfilePicture
    {
        get => Parameter.Name;
        set => SetProperty(Parameter.Base64ProfilePictureData, value, Parameter, (u, n) => u.Base64ProfilePictureData = n);
    }

    public string AccountColorHex
    {
        get => Parameter.AccountColorHex;
        set => SetProperty(Parameter.AccountColorHex, value, Parameter, (u, n) => u.AccountColorHex = n);
    }

    public IEnumerable<MailAccount> HoldingAccounts => new List<MailAccount> { Parameter };

    public AccountMenuItem(MailAccount account, IMenuItem parent = null) : base(account, account.Id, parent)
    {
        UpdateAccount(account);
    }

    public void ApplySynchronizationProgress(AccountSynchronizationProgress progress)
    {
        if (progress == null || progress.AccountId != AccountId)
            return;

        IsSynchronizationInProgress = progress.IsInProgress;
        TotalItemsToSync = progress.TotalUnits;
        RemainingItemsToSync = progress.RemainingUnits;
        SynchronizationStatus = progress.Status ?? string.Empty;
    }

    public void UpdateAccount(MailAccount account)
    {
        Parameter = account;
        AttentionReason = account.AttentionReason;

        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(Base64ProfilePicture));
        OnPropertyChanged(nameof(AccountColorHex));
        OnPropertyChanged(nameof(IsAttentionRequired));

        if (SubMenuItems == null)
            return;

        foreach (var item in SubMenuItems)
        {
            if (item is IFolderMenuItem folderMenuItem)
            {
                folderMenuItem.UpdateParentAccounnt(account);
            }
        }
    }

    private void UpdateFixAccountIssueMenuItem()
    {
        if (AttentionReason != AccountAttentionReason.None && !SubMenuItems.Any(a => a is FixAccountIssuesMenuItem))
        {
            SubMenuItems.Insert(0, new FixAccountIssuesMenuItem(Parameter, this));
        }
        else
        {
            var fixAccountIssueItem = SubMenuItems.FirstOrDefault(a => a is FixAccountIssuesMenuItem);

            if (fixAccountIssueItem != null)
            {
                SubMenuItems.Remove(fixAccountIssueItem);
            }
        }
    }
}
