using System;
using Wino.Domain.Interfaces;

namespace Wino.Mail.ViewModels.Data.MenuItems
{
    public class MergedAccountMoreFolderMenuItem : MenuItemBase<object, IMenuItem>
    {
        public MergedAccountMoreFolderMenuItem(object parameter, Guid? entityId, IMenuItem parentMenuItem = null) : base(parameter, entityId, parentMenuItem)
        {
        }
    }
}
