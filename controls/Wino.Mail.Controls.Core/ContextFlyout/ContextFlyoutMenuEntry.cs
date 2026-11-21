using System.Windows.Input;

namespace Wino.Mail.Controls.Core.ContextFlyout;

/// <summary>
/// Base of every context flyout menu entry. Entries are immutable definitions: the flyout hides on
/// invocation, so no entry has to observe state changes while it is displayed.
/// </summary>
public abstract class ContextFlyoutMenuEntry
{
}

/// <summary>
/// Visual break between command groups. Leading, duplicate, and trailing separators are removed by
/// <see cref="ContextFlyoutFilter"/> when the page is projected.
/// </summary>
public sealed partial class ContextFlyoutSeparatorEntry : ContextFlyoutMenuEntry
{
    public static ContextFlyoutSeparatorEntry Instance { get; } = new();
}

/// <summary>
/// Entry that runs a command when it is invoked.
/// </summary>
public partial class ContextFlyoutCommandEntry : ContextFlyoutMenuEntry
{
    public string Text { get; init; } = string.Empty;

    public string SearchKeywords { get; init; } = string.Empty;

    public ContextFlyoutIcon? Icon { get; init; }

    public ICommand? Command { get; init; }

    public object? CommandParameter { get; init; }

    public bool IsEnabled { get; init; } = true;

    public bool IsDestructive { get; init; }

    public ContextFlyoutShortcut? Shortcut { get; init; }

    public string AutomationId { get; init; } = string.Empty;

    public bool CanExecute()
        => IsEnabled && Command?.CanExecute(CommandParameter) == true;
}

/// <summary>
/// Command entry that also carries a checked state.
/// </summary>
public partial class ContextFlyoutToggleEntry : ContextFlyoutCommandEntry
{
    public bool IsChecked { get; init; }
}

/// <summary>
/// Toggle entry that belongs to a mutually exclusive group.
/// </summary>
public sealed partial class ContextFlyoutRadioEntry : ContextFlyoutToggleEntry
{
    public string GroupName { get; init; } = string.Empty;
}

/// <summary>
/// Entry that pages into its own children. It deliberately carries no command: selecting it
/// navigates, it never executes.
/// </summary>
public sealed partial class ContextFlyoutSubMenuEntry : ContextFlyoutMenuEntry
{
    public string Text { get; init; } = string.Empty;

    public string SearchKeywords { get; init; } = string.Empty;

    public ContextFlyoutIcon? Icon { get; init; }

    public bool IsEnabled { get; init; } = true;

    public string AutomationId { get; init; } = string.Empty;

    /// <summary>
    /// Searchable submenus get a search row that filters their descendants in place. Meant for
    /// dynamic lists such as folders and categories, not for fixed command groups.
    /// </summary>
    public bool IsSearchable { get; init; }

    public IReadOnlyList<ContextFlyoutMenuEntry> Items { get; init; } = [];
}

/// <summary>
/// Frequent root action presented above the item list. Header entries are only shown on the root
/// page.
/// </summary>
public sealed partial class ContextFlyoutHeaderEntry
{
    /// <summary>A divider between two groups of header actions.</summary>
    public static ContextFlyoutHeaderEntry Separator { get; } = new() { IsSeparator = true, IsEnabled = false };

    public bool IsSeparator { get; private init; }

    public string Label { get; init; } = string.Empty;

    public ContextFlyoutIcon? Icon { get; init; }

    public ICommand? Command { get; init; }

    public object? CommandParameter { get; init; }

    public bool IsEnabled { get; init; } = true;

    public ContextFlyoutShortcut? Shortcut { get; init; }

    public bool IsDestructive { get; init; }

    public string AutomationId { get; init; } = string.Empty;

    public bool CanExecute()
        => IsEnabled && Command?.CanExecute(CommandParameter) == true;
}
