using System;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.MenuItems
{
    public class MergedAccountMoreFolderMenuItem : MenuItemBase<object, IMenuItem>
    {
        public MergedAccountMoreFolderMenuItem(object parameter, Guid? entityId, IMenuItem parentMenuItem = null) : base(parameter, entityId, parentMenuItem)
        {
        }
    }
}
