using System;
using System.Collections.ObjectModel;
using Wino.Core.Domain.ComponentModel;
using Wino.Core.Domain.Interfaces;

namespace Wino.Core.Domain.MenuItems;

public partial class MenuItemBase : ObservableModel, IMenuItem
{
    private bool _isExpanded;
    private bool _isSelected;
    private bool _isEnabled = true;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// Whether the pane entry can be invoked. A mode drills into a detail page while its
    /// menu stays published, so its entries are disabled rather than left clickable and dead.
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

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
                if (!IsAccountGroup(parentMenuItem))
                    parentMenuItem.IsExpanded = true;

                parentMenuItem = parentMenuItem.ParentMenuItem;
            }
        }

        // Finally expand itself.
        if (!IsAccountGroup(this))
            IsExpanded = true;
    }

    /// <summary>
    /// An account group keeps the expansion its user chose; revealing an entry, such as the startup inbox, does not change it.
    /// </summary>
    private static bool IsAccountGroup(IMenuItem item) => item is MergedAccountMenuItem or AccountGroupMenuItem;
}

public partial class MenuItemBase<T> : MenuItemBase
{
    private T _parameter;

    public T Parameter
    {
        get => _parameter;
        set => SetProperty(ref _parameter, value);
    }

    public MenuItemBase(T parameter, Guid? entityId, IMenuItem parentMenuItem = null) : base(entityId, parentMenuItem) => Parameter = parameter;
}

public partial class MenuItemBase<TValue, TCollection> : MenuItemBase<TValue>
{
    private bool _isChildSelected;

    public bool IsChildSelected
    {
        get => _isChildSelected;
        set => SetProperty(ref _isChildSelected, value);
    }

    protected MenuItemBase(TValue parameter, Guid? entityId, IMenuItem parentMenuItem = null) : base(parameter, entityId, parentMenuItem) { }

    public ObservableCollection<TCollection> SubMenuItems { get; set; } = [];
}
