using AppKit;

namespace Wino.Presentation.AppKit;

public static class WinoMenus
{
    /// <summary>
    /// Releases a menu with its items and submenus. A menu item keeps its managed handler alive through
    /// its native object, so a menu owned by a disposed view or page must be taken apart with it, or the
    /// handler keeps the owner in memory.
    /// </summary>
    public static void DisposeTree(NSMenu menu)
    {
        if (menu.Handle == IntPtr.Zero) return;
        foreach (var item in menu.Items)
        {
            if (item.Submenu is { } submenu) DisposeTree(submenu);
            item.Dispose();
        }
        menu.RemoveAllItems();
        menu.Dispose();
    }
}
