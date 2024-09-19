using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities.Mail;
using Wino.Core.Domain.Entities.Shared;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.MenuItems
{
    public partial class MergedAccountMenuItem : MenuItemBase<MergedInbox, IMenuItem>, IMergedAccountMenuItem
    {
        public int MergedAccountCount => HoldingAccounts?.Count() ?? 0;

        public IEnumerable<MailAccount> HoldingAccounts { get; }

        [ObservableProperty]
        private int unreadItemCount;

        [ObservableProperty]
        private double synchronizationProgress;

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

        public void UpdateAccount(MailAccount account)
        {

        }
    }
}
