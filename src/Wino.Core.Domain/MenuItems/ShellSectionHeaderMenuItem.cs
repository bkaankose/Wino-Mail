namespace Wino.Core.Domain.MenuItems;

/// <summary>
/// A non-interactive caption between groups of navigation items. Replaces the list group
/// headers the contacts and calendar panes used before they became navigation items.
/// </summary>
public sealed class ShellSectionHeaderMenuItem(string title) : MenuItemBase
{
    public string Title { get; } = title;
}
