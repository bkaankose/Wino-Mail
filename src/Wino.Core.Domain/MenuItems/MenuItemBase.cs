using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.MenuItems;

public partial class MenuItemBase : ObservableObject, IMenuItem
{
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public IMenuItem ParentMenuItem { get; }

    public Guid? EntityId { get; }

    public MenuItemBase(Guid? entityId = null, IMenuItem parentMenuItem = null)
    {
        EntityId = entityId;
        ParentMenuItem = parentMenuItem;
    }

    public void Expand()
    {
        // Recursively expand all parent menu items if parent exists, starting from parent.
        if (ParentMenuItem != null)
        {
            IMenuItem parentMenuItem = ParentMenuItem;

            while (parentMenuItem != null)
            {
                parentMenuItem.IsExpanded = true;

                parentMenuItem = parentMenuItem.ParentMenuItem;
            }
        }

        // Finally expand itself.
        IsExpanded = true;
    }
}

public partial class MenuItemBase<T> : MenuItemBase
{
    [ObservableProperty]
    public partial T Parameter { get; set; }

    public MenuItemBase(T parameter, Guid? entityId, IMenuItem parentMenuItem = null) : base(entityId, parentMenuItem) => Parameter = parameter;
}

public partial class MenuItemBase<TValue, TCollection> : MenuItemBase<TValue>
{
    [ObservableProperty]
    public partial bool IsChildSelected { get; set; }

    protected MenuItemBase(TValue parameter, Guid? entityId, IMenuItem parentMenuItem = null) : base(parameter, entityId, parentMenuItem) { }

    public ObservableCollection<TCollection> SubMenuItems { get; set; } = [];
}
