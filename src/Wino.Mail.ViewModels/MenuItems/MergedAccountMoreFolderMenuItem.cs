using System;
using Wino.Core.Domain.Interfaces;
using Wino.Mail.ViewModels.MenuItems.Contracts;

namespace Wino.Mail.ViewModels.MenuItems;

public class MergedAccountMoreFolderMenuItem : MenuItemBase<object, IMenuItem>
{
    public MergedAccountMoreFolderMenuItem(object parameter, Guid? entityId, IMenuItem parentMenuItem = null) : base(parameter, entityId, parentMenuItem)
    {
    }
}
