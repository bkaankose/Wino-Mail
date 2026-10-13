using UIKit;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>
/// The idiom shell's presentation surface (PhoneShellHost, PadShellHost). <see cref="UIKitNavigationService"/>
/// creates, activates and releases controllers; the host only places them. Members run on the main thread.
/// </summary>
public interface IUIKitShellHost
{
    /// <summary>The scene window, once the scene connected.</summary>
    UIWindow? Window { get; }

    /// <summary>Completes when the scene window exists; startup waits for it before it shows the first page.</summary>
    Task<UIWindow> WindowReady { get; }

    /// <summary>Hands the scene window to the host. The scene delegate calls it once per connected scene.</summary>
    void AttachWindow(UIWindow window);

    /// <summary>Makes <paramref name="controller"/> the window's root (onboarding, or the shell).</summary>
    void ShowRoot(UIViewController controller);

    /// <summary>Creates this idiom's shell controller.</summary>
    UIKitShellViewController CreateShell();

    /// <summary>
    /// Shows a rendering-frame page: the reader column (iPad) or a push on the current stack (iPhone).
    /// False when the shell has no frame for it.
    /// </summary>
    bool ShowInRenderingFrame(UIKitShellViewController shell, UIViewController controller);

    /// <summary>Pushes <paramref name="controller"/> on the open sheet's stack, else on the shell's current stack.</summary>
    bool Push(UIKitShellViewController? shell, UIViewController controller);

    /// <summary>
    /// Presents <paramref name="controller"/> as the root of a navigation sheet (<see cref="UIKitPageHost.Sheet"/> or
    /// <see cref="UIKitPageHost.FullScreenSheet"/>). <paramref name="dismissedByUser"/> runs when the user swipes it away.
    /// </summary>
    UINavigationController PresentSheet(UIViewController controller, UIKitPageHost style, Action dismissedByUser);

    /// <summary>Dismisses a sheet presented by <see cref="PresentSheet"/>.</summary>
    Task DismissSheetAsync(UINavigationController sheet);

    /// <summary>Pops the top page of the open sheet or the shell's current stack. False when it is at its root.</summary>
    bool GoBack(UIKitShellViewController? shell);

    /// <summary>The top-most presented controller of the window: the anchor for alerts, pickers and sign-in sessions.</summary>
    UIViewController? TopViewController { get; }
}
