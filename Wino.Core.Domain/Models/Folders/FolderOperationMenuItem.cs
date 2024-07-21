using Wino.Domain.Enums;
using Wino.Domain.Interfaces;
using Wino.Domain.Models.Menus;

namespace Wino.Domain.Models.Folders
{
    public class FolderOperationMenuItem : MenuOperationItemBase<FolderOperation>, IMenuOperation
    {
        protected FolderOperationMenuItem(FolderOperation operation, bool isEnabled) : base(operation, isEnabled) { }

        public static FolderOperationMenuItem Create(FolderOperation operation, bool isEnabled = true)
            => new FolderOperationMenuItem(operation, isEnabled);
    }
}
