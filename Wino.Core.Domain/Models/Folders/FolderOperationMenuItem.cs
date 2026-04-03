using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Menus;

namespace Wino.Core.Domain.Models.Folders;

public class FolderOperationMenuItem : MenuOperationItemBase<FolderOperation>
{
    protected FolderOperationMenuItem(FolderOperation operation, bool isEnabled, bool isSecondaryMenuItem = false) : base(operation, isEnabled)
    {
        IsSecondaryMenuPreferred = isSecondaryMenuItem;
    }

    public static FolderOperationMenuItem Create(FolderOperation operation, bool isEnabled = true, bool isSecondaryMenuItem = false)
        => new FolderOperationMenuItem(operation, isEnabled, isSecondaryMenuItem);
}
