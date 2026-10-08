#nullable enable

using Wino.Core.Domain.MenuItems;

namespace Wino.Core.Domain.Models.Navigation;

/// <summary>
/// The navigation menu definition a mode view model publishes to the shell.
/// The shell only hosts these collections; it never builds or mutates them.
/// </summary>
public sealed class ShellMenu
{
    /// <summary>
    /// Items rendered in the navigation pane's scrollable item area.
    /// </summary>
    public required MenuItemCollection Items { get; init; }

    /// <summary>
    /// Items pinned to the bottom of the navigation pane. Null when the mode has none.
    /// </summary>
    public MenuItemCollection? FooterItems { get; init; }

    /// <summary>
    /// Whether the hosting <c>NavigationView</c> selection is meaningful for this mode.
    /// Modes that only react to invocations (calendar, contacts) set this to false so the
    /// shell does not push selection changes back into the provider.
    /// </summary>
    public bool HandlesSelection { get; init; }
}
