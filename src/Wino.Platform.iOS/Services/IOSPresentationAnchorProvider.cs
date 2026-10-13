using AuthenticationServices;
using Foundation;
using UIKit;

namespace Wino.Platform.iOS.Services;

/// <summary>
/// Presentation anchor for <see cref="ASWebAuthenticationSession"/>: the window of the head's current view controller,
/// otherwise the key window of the foreground-active scene, otherwise any window of a connected scene.
/// UIKit calls it on the main thread. Sessions start only when <see cref="HasAnchor"/> is true.
/// </summary>
public sealed class IOSPresentationAnchorProvider(Func<UIViewController?> presenter) : NSObject, IASWebAuthenticationPresentationContextProviding
{
    [Export("presentationAnchorForWebAuthenticationSession:")]
    public UIWindow GetPresentationAnchor(ASWebAuthenticationSession session)
        => FindAnchor() ?? new UIWindow(FirstWindowScene() ?? throw new InvalidOperationException("No window scene is connected."));

    /// <summary>True when a window exists to anchor the session; read on the main thread.</summary>
    public bool HasAnchor => FindAnchor() is not null;

    private UIWindow? FindAnchor()
    {
        var window = presenter()?.ViewIfLoaded?.Window;
        if (window is not null) return window;

        UIWindow? fallback = null;
        foreach (var scene in UIApplication.SharedApplication.ConnectedScenes)
        {
            if (scene is not UIWindowScene windowScene) continue;
            var candidate = windowScene.KeyWindow ?? windowScene.Windows.FirstOrDefault();
            if (candidate is null) continue;
            if (windowScene.ActivationState == UISceneActivationState.ForegroundActive) return candidate;
            fallback ??= candidate;
        }
        return fallback;
    }

    private static UIWindowScene? FirstWindowScene()
        => UIApplication.SharedApplication.ConnectedScenes.OfType<UIWindowScene>().FirstOrDefault();
}
