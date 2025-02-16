using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Interfaces;
using Wino.Core.Domain.Models.Menus;

namespace Wino.Core.Domain.Models.Folders
{
    public class FolderOperationMenuItem : MenuOperationItemBase<FolderOperation>, IMenuOperation
    {
        protected FolderOperationMenuItem(FolderOperation operation, bool isEnabled) : base(operation, isEnabled) { }

        public static FolderOperationMenuItem Create(FolderOperation operation, bool isEnabled = true)
            => new FolderOperationMenuItem(operation, isEnabled);
    }
}
