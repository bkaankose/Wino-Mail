using AppKit;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.MacOS.Infrastructure;

/// <summary>
/// Where a native page is shown. Mirrors the Windows frames: the window itself (onboarding),
/// the shell's content area (MailListPage), the mail page's reading pane (rendering/compose),
/// and the separate Settings window that replaces the Windows Settings shell mode.
/// </summary>
public enum MacPageHost
{
    Window,
    ShellContent,
    RenderingFrame,
    SettingsWindow
}

/// <summary>The mail page's reading pane. The active mail page attaches itself to the router.</summary>
public interface IRenderingFrameHost
{
    Task ShowAsync(NSViewController controller, object? parameter);
    Task ClearAsync();
}

/// <summary>The Settings window (Cmd+,). Implemented by the Settings feature and resolved optionally.</summary>
public interface ISettingsWindowPresenter
{
    /// <summary>Opens or focuses the window. A null page keeps the current page or shows the default one.</summary>
    Task ShowAsync(WinoPage? page = null, object? parameter = null);

    /// <summary>True when the Settings window is key and handled a back request.</summary>
    bool TryGoBack();

    /// <summary>Closes the window and releases its page and history; the presenter stays usable.</summary>
    Task CloseAsync();

    Task StopAsync();
}

/// <summary>Mail actions exposed in the unified toolbar and the Message menu.</summary>
public enum ShellCommand
{
    Archive,
    Delete,
    Move,
    Flag,
    ToggleRead,
    Reply,
    ReplyAll,
    Forward
}

/// <summary>Implemented by the shell's current content page to receive toolbar and menu commands.</summary>
public interface IShellCommandTarget
{
    bool CanExecute(ShellCommand command);

    /// <param name="anchor">The toolbar item's view when available, for popovers such as Move.</param>
    void Execute(ShellCommand command, NSView? anchor);

    /// <summary>Raise when selection or state changes so the toolbar revalidates.</summary>
    event EventHandler? CommandStateChanged;
}

/// <summary>Implemented by the shell's current content page to receive the toolbar search field.</summary>
public interface IShellSearchTarget
{
    Task SearchTextChangedAsync(string text);
    Task SearchSubmittedAsync(string text);
    Task SearchClearedAsync();
}

/// <summary>
/// Supplies the Daily briefing panel (Windows DailyBriefingPanel). Implemented by the extras
/// feature (Views/Extras); the shell resolves it optionally and hosts the panel as a right-edge
/// overlay, 400pt wide, under the title bar. Null when the feature is not registered.
/// </summary>
public interface IDailyBriefingPresenter
{
    /// <summary>Creates the panel controller once; the shell keeps and re-shows it.</summary>
    NSViewController CreatePanel(Action close);

    /// <summary>True when there are unseen briefing items (drives the toolbar badge).</summary>
    bool HasUnseenItems { get; }

    event EventHandler? UnseenItemsChanged;
}

/// <summary>Opens the What's New window (Windows WhatsNewWindow, 880×640). Implemented by Views/Extras.</summary>
public interface IWhatsNewPresenter
{
    Task ShowAsync();
}

/// <summary>Opens the Wino Account flyout or sign-in from the title bar account button. Implemented by Views/Extras.</summary>
public interface IWinoAccountPresenter
{
    void Show(NSView anchor);
}
