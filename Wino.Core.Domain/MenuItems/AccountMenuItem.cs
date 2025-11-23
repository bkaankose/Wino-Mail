using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Core.Domain.MenuItems;

public partial class AccountMenuItem : MenuItemBase<MailAccount, MenuItemBase<IMailItemFolder, FolderMenuItem>>, IAccountMenuItem
{
    [ObservableProperty]
    private int unreadItemCount;

    /// <summary>
    /// Total items to sync. 0 means indeterminate progress.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSynchronizationProgressVisible), nameof(SynchronizationProgress), nameof(IsProgressIndeterminate))]
    public partial int TotalItemsToSync { get; set; }

    /// <summary>
    /// Remaining items to sync.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SynchronizationProgress))]
    public partial int RemainingItemsToSync { get; set; }

    /// <summary>
    /// Current synchronization status message.
    /// </summary>
    [ObservableProperty]
    public partial string SynchronizationStatus { get; set; } = string.Empty;

    [ObservableProperty]
    private bool _isEnabled = true;

    public bool IsAttentionRequired => AttentionReason != AccountAttentionReason.None;

    /// <summary>
    /// Calculates synchronization progress percentage (0-100).
    /// Returns -1 for indeterminate progress when TotalItemsToSync is 0.
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

    /// <summary>
    /// Whether synchronization progress should be visible.
    /// Visible when there's active synchronization (TotalItemsToSync > 0 or RemainingItemsToSync > 0).
    /// </summary>
    public bool IsSynchronizationProgressVisible => TotalItemsToSync > 0 || RemainingItemsToSync > 0;

    /// <summary>
    /// Whether progress should be indeterminate (when total is 0 but there's still synchronization happening).
    /// </summary>
    public bool IsProgressIndeterminate => TotalItemsToSync == 0 && RemainingItemsToSync == 0 && IsSynchronizationProgressVisible;

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

    public void UpdateAccount(MailAccount account)
    {
        Parameter = account;

        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(Base64ProfilePicture));
        OnPropertyChanged(nameof(AccountColorHex));
        OnPropertyChanged(nameof(IsAttentionRequired));

        if (SubMenuItems == null) return;

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
            // Add fix issue item if not exists.
            SubMenuItems.Insert(0, new FixAccountIssuesMenuItem(Parameter, this));
        }
        else
        {
            // Remove existing if issue is resolved.
            var fixAccountIssueItem = SubMenuItems.FirstOrDefault(a => a is FixAccountIssuesMenuItem);

            if (fixAccountIssueItem != null)
            {
                SubMenuItems.Remove(fixAccountIssueItem);
            }
        }
    }
}
