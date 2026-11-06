using System.Windows.Input;
#if WINRT_EXPOSED
using WinRT;
#endif

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
#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial class ContextFlyoutSeparatorEntry : ContextFlyoutMenuEntry
{
    public static ContextFlyoutSeparatorEntry Instance { get; } = new();
}

/// <summary>
/// Entry that runs a command when it is invoked.
/// </summary>
#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
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
#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public partial class ContextFlyoutToggleEntry : ContextFlyoutCommandEntry
{
    public bool IsChecked { get; init; }
}

/// <summary>
/// Toggle entry that belongs to a mutually exclusive group.
/// </summary>
#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial class ContextFlyoutRadioEntry : ContextFlyoutToggleEntry
{
    public string GroupName { get; init; } = string.Empty;
}

/// <summary>
/// Entry that pages into its own children. It deliberately carries no command: selecting it
/// navigates, it never executes.
/// </summary>
#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial class ContextFlyoutSubMenuEntry : ContextFlyoutMenuEntry
{
    public string Text { get; init; } = string.Empty;

    public string SearchKeywords { get; init; } = string.Empty;

    public ContextFlyoutIcon? Icon { get; init; }

    public bool IsEnabled { get; init; } = true;

    public string AutomationId { get; init; } = string.Empty;

    public IReadOnlyList<ContextFlyoutMenuEntry> Items { get; init; } = [];
}

/// <summary>
/// Frequent root action presented above the item list. Header entries are only shown on the root
/// page.
/// </summary>
#if WINRT_EXPOSED
[GeneratedWinRTExposedType]
#endif
public sealed partial class ContextFlyoutHeaderEntry
{
    public string Label { get; init; } = string.Empty;

    public ContextFlyoutIcon? Icon { get; init; }

    public ICommand? Command { get; init; }

    public object? CommandParameter { get; init; }

    public bool IsEnabled { get; init; } = true;

    public ContextFlyoutShortcut? Shortcut { get; init; }

    public string AutomationId { get; init; } = string.Empty;

    public bool CanExecute()
        => IsEnabled && Command?.CanExecute(CommandParameter) == true;
}
