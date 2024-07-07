using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain;
using Wino.Core.Domain.Entities;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Folders;

namespace Wino.Core.MenuItems
{
    public partial class FolderMenuItem : MenuItemBase<IMailItemFolder, FolderMenuItem>, IFolderMenuItem
    {
        [ObservableProperty]
        private int unreadItemCount;

        public bool HasTextColor => !string.IsNullOrEmpty(Parameter.TextColorHex);
        public bool IsMoveTarget => HandlingFolders.All(a => a.IsMoveTarget);

        public SpecialFolderType SpecialFolderType => Parameter.SpecialFolderType;
        public bool IsSticky => Parameter.IsSticky;
        public bool IsSystemFolder => Parameter.IsSystemFolder;

        /// <summary>
        /// Display name of the folder. More and Category folders have localized display names.
        /// </summary>
        public string FolderName
        {
            get
            {
                if (Parameter.SpecialFolderType == SpecialFolderType.More)
                    return Translator.MoreFolderNameOverride;
                else if (Parameter.SpecialFolderType == SpecialFolderType.Category)
                    return Translator.CategoriesFolderNameOverride;
                else
                    return Parameter.FolderName;
            }
            set => SetProperty(Parameter.FolderName, value, Parameter, (u, n) => u.FolderName = n);
        }

        public bool IsSynchronizationEnabled
        {
            get => Parameter.IsSynchronizationEnabled;
            set => SetProperty(Parameter.IsSynchronizationEnabled, value, Parameter, (u, n) => u.IsSynchronizationEnabled = n);
        }

        public IEnumerable<IMailItemFolder> HandlingFolders => new List<IMailItemFolder>() { Parameter };

        public MailAccount ParentAccount { get; private set; }

        public string AssignedAccountName => ParentAccount?.Name;

        public bool ShowUnreadCount => Parameter.ShowUnreadCount;

        IEnumerable<IMenuItem> IBaseFolderMenuItem.SubMenuItems => SubMenuItems;

        public FolderMenuItem(IMailItemFolder folderStructure, MailAccount parentAccount, IMenuItem parentMenuItem) : base(folderStructure, folderStructure.Id, parentMenuItem)
        {
            ParentAccount = parentAccount;
        }

        public void UpdateFolder(IMailItemFolder folder)
        {
            Parameter = folder;

            OnPropertyChanged(nameof(IsSynchronizationEnabled));
            OnPropertyChanged(nameof(ShowUnreadCount));
            OnPropertyChanged(nameof(HasTextColor));
            OnPropertyChanged(nameof(IsSystemFolder));
            OnPropertyChanged(nameof(SpecialFolderType));
            OnPropertyChanged(nameof(IsSticky));
            OnPropertyChanged(nameof(FolderName));
        }

        public override string ToString() => FolderName;

        public void UpdateParentAccounnt(MailAccount account) => ParentAccount = account;
    }
}
