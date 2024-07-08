using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Core.MenuItems
{
    public partial class AccountMenuItem : MenuItemBase<MailAccount, MenuItemBase<IMailItemFolder, FolderMenuItem>>, IAccountMenuItem
    {
        [ObservableProperty]
        private int unreadItemCount;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSynchronizationProgressVisible))]
        private double synchronizationProgress;

        [ObservableProperty]
        private bool _isEnabled = true;

        public bool IsAttentionRequired => AttentionReason != AccountAttentionReason.None;
        public bool IsSynchronizationProgressVisible => SynchronizationProgress > 0 && SynchronizationProgress < 100;
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

        public IEnumerable<MailAccount> HoldingAccounts => new List<MailAccount> { Parameter };

        public AccountMenuItem(MailAccount account, IMenuItem parent = null) : base(account, account.Id, parent)
        {
            UpdateAccount(account);
        }

        public void UpdateAccount(MailAccount account)
        {
            Parameter = account;
            AccountName = account.Name;
            AttentionReason = account.AttentionReason;

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
}
