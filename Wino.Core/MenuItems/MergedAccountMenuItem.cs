using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.MenuItems
{
    public partial class MergedAccountMenuItem : MenuItemBase<MergedInbox, IMenuItem>, IAccountMenuItem
    {
        public int MergedAccountCount => GetAccountMenuItems().Count();

        public IEnumerable<MailAccount> HoldingAccounts => GetAccountMenuItems()?.SelectMany(a => a.HoldingAccounts);

        [ObservableProperty]
        private int unreadItemCount;

        [ObservableProperty]
        private double synchronizationProgress;

        [ObservableProperty]
        private string mergedAccountName;

        public MergedAccountMenuItem(MergedInbox mergedInbox, IMenuItem parent) : base(mergedInbox, mergedInbox.Id, parent)
        {
            MergedAccountName = mergedInbox.Name;
        }

        public void RefreshFolderItemCount()
        {
            UnreadItemCount = GetAccountMenuItems().Select(a => a.GetUnreadItemCountByFolderType(SpecialFolderType.Inbox)).Sum();

            var unreadUpdateFolders = SubMenuItems.OfType<IBaseFolderMenuItem>().Where(a => a.ShowUnreadCount);

            foreach (var folder in unreadUpdateFolders)
            {
                folder.UnreadItemCount = GetAccountMenuItems().Select(a => a.GetUnreadItemCountByFolderType(folder.SpecialFolderType)).Sum();
            }
        }

        // Accounts are always located in More folder of Merged Inbox menu item.
        public IEnumerable<AccountMenuItem> GetAccountMenuItems()
        {
            var moreFolder = SubMenuItems.OfType<MergedAccountMoreFolderMenuItem>().FirstOrDefault();

            if (moreFolder == null) return default;

            return moreFolder.SubMenuItems.OfType<AccountMenuItem>();
        }

        public void UpdateAccount(MailAccount account)
            => GetAccountMenuItems().FirstOrDefault(a => a.HoldingAccounts.Any(b => b.Id == account.Id))?.UpdateAccount(account);
    }
}
