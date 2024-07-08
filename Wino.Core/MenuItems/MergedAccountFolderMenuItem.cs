using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Core.MenuItems
{
    /// <summary>
    /// Menu item that holds a list of folders under the merged account menu item.
    /// </summary>
    public partial class MergedAccountFolderMenuItem : MenuItemBase<List<IMailItemFolder>, IMenuItem>, IMergedAccountFolderMenuItem
    {
        public SpecialFolderType FolderType { get; }

        public string FolderName { get; private set; }

        // Any of the folders is enough to determine the synchronization enable/disable state.
        public bool IsSynchronizationEnabled => HandlingFolders.Any(a => a.IsSynchronizationEnabled);
        public bool IsMoveTarget => HandlingFolders.All(a => a.IsMoveTarget);
        public IEnumerable<IMailItemFolder> HandlingFolders => Parameter;

        // All folders in the list should have the same type.
        public SpecialFolderType SpecialFolderType => HandlingFolders.First().SpecialFolderType;

        public bool IsSticky => true;

        public bool IsSystemFolder => true;

        public string AssignedAccountName => MergedInbox?.Name;

        public MergedInbox MergedInbox { get; set; }

        public bool ShowUnreadCount => HandlingFolders?.Any(a => a.ShowUnreadCount) ?? false;

        public new IEnumerable<IMenuItem> SubMenuItems => base.SubMenuItems;

        [ObservableProperty]
        private int unreadItemCount;

        // Merged account's shared folder menu item does not have an entity id.
        // Navigations to specific folders are done by explicit folder id if needed.

        public MergedAccountFolderMenuItem(List<IMailItemFolder> parameter, IMenuItem parentMenuItem, MergedInbox mergedInbox) : base(parameter, null, parentMenuItem)
        {
            Guard.IsNotNull(mergedInbox, nameof(mergedInbox));
            Guard.IsNotNull(parameter, nameof(parameter));
            Guard.HasSizeGreaterThan(parameter, 0, nameof(parameter));

            MergedInbox = mergedInbox;

            SetFolderName();

            // All folders in the list should have the same type.
            FolderType = parameter[0].SpecialFolderType;
        }

        private void SetFolderName()
        {
            // Folders that hold more than 1 folder belong to merged account.
            // These folders will be displayed as their localized names based on the
            // special type they have.

            if (HandlingFolders.Count() > 1)
            {
                FolderName = GetSpecialFolderName(HandlingFolders.First());
            }
            else
            {
                // Folder only holds 1 Id, but it's displayed as merged account folder.
                FolderName = HandlingFolders.First().FolderName;
            }
        }

        private string GetSpecialFolderName(IMailItemFolder folder)
        {
            var specialType = folder.SpecialFolderType;

            // We only handle 5 different types for combining folders.
            // Rest of the types are not supported.

            return specialType switch
            {
                SpecialFolderType.Inbox => Translator.MergedAccountCommonFolderInbox,
                SpecialFolderType.Draft => Translator.MergedAccountCommonFolderDraft,
                SpecialFolderType.Sent => Translator.MergedAccountCommonFolderSent,
                SpecialFolderType.Deleted => Translator.MergedAccountCommonFolderTrash,
                SpecialFolderType.Junk => Translator.MergedAccountCommonFolderJunk,
                SpecialFolderType.Archive => Translator.MergedAccountCommonFolderArchive,
                _ => folder.FolderName,
            };
        }

        public void UpdateFolder(IMailItemFolder folder)
        {
            var existingFolder = Parameter.FirstOrDefault(a => a.Id == folder.Id);

            if (existingFolder == null) return;

            Parameter.Remove(existingFolder);
            Parameter.Add(folder);

            SetFolderName();
            OnPropertyChanged(nameof(ShowUnreadCount));
            OnPropertyChanged(nameof(IsSynchronizationEnabled));
        }
    }
}
