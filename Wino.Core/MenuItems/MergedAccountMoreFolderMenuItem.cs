using System;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.MenuItems
{
    public class MergedAccountMoreFolderMenuItem : MenuItemBase<object, IMenuItem>
    {
        public MergedAccountMoreFolderMenuItem(object parameter, Guid? entityId, IMenuItem parentMenuItem = null) : base(parameter, entityId, parentMenuItem)
        {
        }
    }
}
