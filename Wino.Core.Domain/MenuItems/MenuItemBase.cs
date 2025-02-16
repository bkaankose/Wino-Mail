using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.MenuItems;

public partial class MenuItemBase : ObservableObject, IMenuItem
{
    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

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
    private T _parameter;

    public MenuItemBase(T parameter, Guid? entityId, IMenuItem parentMenuItem = null) : base(entityId, parentMenuItem) => Parameter = parameter;
}

public partial class MenuItemBase<TValue, TCollection> : MenuItemBase<TValue>
{
    [ObservableProperty]
    private bool _isChildSelected;

    protected MenuItemBase(TValue parameter, Guid? entityId, IMenuItem parentMenuItem = null) : base(parameter, entityId, parentMenuItem) { }

    public ObservableCollection<TCollection> SubMenuItems { get; set; } = [];
}
