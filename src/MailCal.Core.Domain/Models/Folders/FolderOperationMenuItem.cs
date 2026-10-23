using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Models.Menus;

namespace MailCal.Core.Domain.Models.Folders;

public class FolderOperationMenuItem : MenuOperationItemBase<FolderOperation>
{
    private FolderOperationMenuItem(FolderOperation operation, bool isEnabled, bool isSecondaryMenuItem)
        : base(operation, isEnabled, isSecondaryMenuItem)
    { }

    public static FolderOperationMenuItem Create(FolderOperation operation, bool isEnabled = true, bool isSecondaryMenuItem = false)
        => new FolderOperationMenuItem(operation, isEnabled, isSecondaryMenuItem);
}
