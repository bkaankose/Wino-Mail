using System;
using MailCal.Core.Domain.Interfaces;

namespace MailCal.Core.Domain.MenuItems;

public class MergedAccountMoreFolderMenuItem : MenuItemBase<object, IMenuItem>
{
    public MergedAccountMoreFolderMenuItem(object parameter, Guid? entityId, IMenuItem parentMenuItem = null) : base(parameter, entityId, parentMenuItem)
    {
    }
}
