using System;

namespace Wino.Core.Domain.Interfaces;

public interface IMenuItem
{
    /// <summary>
    /// An id that this menu item holds.
    /// For an account, it's AccountId.
    /// For folder, it's FolderId.
    /// For merged account, it's MergedAccountId.
    /// Null if it's a menu item that doesn't hold any valuable entity.
    /// </summary>
    Guid? EntityId { get; }

    /// <summary>
    /// Is any of the sub items that this menu item contains selected.
    /// </summary>
    // bool IsChildSelected { get; }

    /// <summary>
    /// Whether the menu item is expanded or not.
    /// </summary>
    bool IsExpanded { get; set; }

    /// <summary>
    /// Whether the menu item is selected or not.
    /// </summary>
    bool IsSelected { get; set; }

    /// <summary>
    /// Parent menu item that contains this menu item.
    /// </summary>
    IMenuItem ParentMenuItem { get; }

    /// <summary>
    /// Recursively expand all parent menu items if parent exists, starting from parent.
    /// </summary>
    void Expand();
}
