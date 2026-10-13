#nullable enable

using System;
using System.Threading.Tasks;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Navigation;

namespace Wino.Mail.ViewModels.Navigation;

/// <summary>
/// Where a routed page is shown. Mirrors the Windows frames: the main window itself (onboarding),
/// the shell's content area (MailListPage), the mail page's reading pane (rendering/compose), and
/// the Settings surface that replaces the Windows Settings shell mode on Apple platforms.
/// </summary>
public enum NavigationHostKind
{
    Window,
    ShellContent,
    RenderingFrame,
    Settings
}

/// <summary>
/// The presentation half of navigation. <see cref="NavigationCoordinator"/> owns routes, history,
/// back confirmation and the account lifecycle reactions; a platform head implements this interface
/// to create, show, activate and release its native pages. Methods are called off the UI thread
/// unless stated otherwise; implementations marshal to the UI thread themselves.
/// </summary>
public interface INavigationHost
{
    /// <summary>Resolves the host of <paramref name="page"/>, letting the parameter override it. False when the head has no route.</summary>
    bool TryGetRoute(WinoPage page, object? parameter, out NavigationHostKind hostKind);

    /// <summary>The native page type registered for <paramref name="page"/>, or null.</summary>
    Type? GetPageType(WinoPage page);

    /// <summary>True while the app shell is the main window content.</summary>
    bool IsShellShown { get; }

    /// <summary>True when the main window shows any page (shell or onboarding).</summary>
    bool HasMainContent { get; }

    /// <summary>True when the main window page reports pending work (for example account setup in progress).</summary>
    bool MainContentHasPendingWork { get; }

    /// <summary>The ViewModel of the page in the shell content area, used for back confirmation and results.</summary>
    object? ShellContentViewModel { get; }

    /// <summary>True while the secondary onboarding surface (Settings › Add account) is open.</summary>
    bool IsSecondaryWindowShown { get; }

    /// <summary>True when the head provides a Settings surface.</summary>
    bool IsSettingsAvailable { get; }

    /// <summary>Shows the page in the active mail page's reading pane; no-op when none is attached.</summary>
    Task ShowInRenderingFrameAsync(WinoPage page, object? parameter);

    /// <summary>Replaces the shell's content page.</summary>
    Task SetShellContentAsync(WinoPage page, object? parameter);

    /// <summary>Empties the shell's content area.</summary>
    Task ClearShellContentAsync();

    /// <summary>
    /// Makes the page the main window content and releases the previous one (the shell included).
    /// Call <paramref name="committed"/> once the new page is hosted, before activating it.
    /// </summary>
    Task ReplaceMainContentAsync(WinoPage page, object? parameter, NavigationMode mode, Action committed);

    /// <summary>
    /// Shows an onboarding page on the secondary surface while the shell stays up. Call
    /// <paramref name="committed"/> once the new page is hosted, before activating it.
    /// </summary>
    Task ShowInSecondaryWindowAsync(WinoPage page, object? parameter, NavigationMode mode, Action committed);

    /// <summary>Releases and closes the secondary surface. <paramref name="closedByUser"/> means the surface is already closing.</summary>
    Task CloseSecondaryWindowAsync(bool closedByUser);

    /// <summary>
    /// Creates the shell, makes it the main window content and releases the previous page. Call
    /// <paramref name="committed"/> once the shell is hosted. Returns the shell's first activation,
    /// which the coordinator runs outside its gate because the shell navigates during activation.
    /// </summary>
    Task<Func<Task>> PresentShellAsync(Action committed);

    /// <summary>Brings the shell's window to the front.</summary>
    Task BringShellToFrontAsync();

    /// <summary>Lets the shell switch to <paramref name="mode"/>; no-op without a shell.</summary>
    Task ActivateShellModeAsync(WinoApplicationMode mode, ShellModeActivationContext? activationContext);

    /// <summary>After the last account was removed: drops shell-scoped surfaces and closes every other app window.</summary>
    Task DismissShellSurfacesAsync();

    /// <summary>Releases the main window content when the app stops.</summary>
    Task ReleaseMainContentAsync();

    /// <summary>Opens or focuses Settings. A null page keeps the current page or shows the default one.</summary>
    Task ShowSettingsAsync(WinoPage? page, object? parameter);

    /// <summary>True when Settings is focused and handled a back request.</summary>
    bool TrySettingsGoBack();

    Task CloseSettingsAsync();

    Task StopSettingsAsync();
}
