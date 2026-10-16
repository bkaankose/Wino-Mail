using AppKit;
using AuthenticationServices;
using Foundation;

namespace Wino.Platform.MacOS.Services;

/// <summary>
/// Presentation anchor for the system sign-in surfaces (<see cref="ASAuthorizationController"/> for Sign in with Apple,
/// <see cref="ASWebAuthenticationSession"/> for the browser flow): the key window, otherwise the head's main window,
/// then down through attached sheets so the system panel stacks on the Wino Account sign-in sheet that asked for it.
/// AppKit calls it on the main thread.
/// </summary>
public sealed class MacPresentationAnchorProvider(Func<NSWindow?> owner) : NSObject,
    IASAuthorizationControllerPresentationContextProviding, IASWebAuthenticationPresentationContextProviding
{
    [Export("presentationAnchorForAuthorizationController:")]
    public NSWindow GetPresentationAnchor(ASAuthorizationController controller) => RequireAnchor();

    [Export("presentationAnchorForWebAuthenticationSession:")]
    public NSWindow GetPresentationAnchor(ASWebAuthenticationSession session) => RequireAnchor();

    /// <summary>True when a window exists to anchor a session; read on the main thread.</summary>
    public bool HasAnchor => FindAnchor() is not null;

    private NSWindow RequireAnchor()
        => FindAnchor() ?? throw new InvalidOperationException("No window is available to present the sign-in sheet.");

    private NSWindow? FindAnchor()
    {
        var application = NSApplication.SharedApplication;
        var window = application.KeyWindow is { IsVisible: true } key ? key : owner() ?? application.MainWindow;
        while (window?.AttachedSheet is { } sheet) window = sheet;
        return window;
    }
}
