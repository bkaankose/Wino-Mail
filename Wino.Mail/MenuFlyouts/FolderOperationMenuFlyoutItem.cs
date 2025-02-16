using System;
using Wino.Core.Domain.Models.Folders;

namespace Wino.MenuFlyouts
{
    public partial class FolderOperationMenuFlyoutItem : WinoOperationFlyoutItem<FolderOperationMenuItem>
    {
        public FolderOperationMenuFlyoutItem(FolderOperationMenuItem operationMenuItem, Action<FolderOperationMenuItem> clicked) : base(operationMenuItem, clicked)
        {
        }
    }
}
