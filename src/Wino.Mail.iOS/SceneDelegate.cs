using Foundation;
using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Pad;
using Wino.Presentation.UIKit;

namespace Wino.Mail.iOS;

/// <summary>
/// The window scene: creates the window over the theme backdrop and hands it to the idiom's shell host (Composition
/// registered the phone or pad shell by <c>UIDevice.UserInterfaceIdiom</c>), routes URL contexts to the shared
/// activation router, and asks for a background refresh when the scene leaves the foreground. On iPad a connecting scene
/// may be a pop-out window (<see cref="PadShellHost.TryConnectPopOut"/>), which never becomes the main window.
/// </summary>
[Register("SceneDelegate")]
public sealed class SceneDelegate : UIResponder, IUIWindowSceneDelegate
{
    [Export("window")]
    public UIWindow? Window { get; set; }

    [Export("scene:willConnectToSession:options:")]
    public void WillConnect(UIScene scene, UISceneSession session, UISceneConnectionOptions connectionOptions)
    {
        if (scene is not UIWindowScene windowScene || AppDelegate.Services is not { } services) return;
        // iPad pop-out windows (the composer's "Open in new window") get their window and page from the pad host.
        if (services.GetService<PadShellHost>() is { } pad && pad.TryConnectPopOut(windowScene, connectionOptions)) return;

        var host = services.GetRequiredService<IUIKitShellHost>();
        if (host.Window is { WindowScene: { } existing } && !ReferenceEquals(existing, windowScene) && existing.ActivationState != UISceneActivationState.Unattached)
        {
            // One main window: a second one (the iPad app switcher's New Window) would take the shell from the first.
            Console.WriteLine("Wino Mail iOS: closing an extra main-window scene");
            UIApplication.SharedApplication.RequestSceneSessionDestruction(session, null, error => Serilog.Log.Warning("Scene close failed: {Error}", error?.LocalizedDescription));
            return;
        }
        var window = new UIWindow(windowScene);
        Window = window;
        // The backdrop shows until startup decides between Welcome and the shell.
        var launch = new UIViewController();
        var root = new UIView();
        WinoLayout.Fill(UIKitThemeService.Track(new ThemeBackdropView()), root);
        launch.View = root;
        window.RootViewController = launch;
        UIKitThemeService.ApplyTo(window);
        window.MakeKeyAndVisible();
        host.AttachWindow(window);
        Console.WriteLine($"Wino Mail iOS: scene connected ({UIDevice.CurrentDevice.UserInterfaceIdiom})");
        Route(services, connectionOptions.UrlContexts);
    }

    [Export("scene:openURLContexts:")]
    public void OpenUrlContexts(UIScene scene, NSSet<UIOpenUrlContext> urlContexts)
    {
        if (AppDelegate.Services is { } services) Route(services, urlContexts);
    }

    [Export("sceneDidDisconnect:")]
    public void DidDisconnect(UIScene scene)
    {
        // A closed pop-out window releases its page (a draft saves).
        AppDelegate.Services?.GetService<PadShellHost>()?.SceneDisconnected(scene);
    }

    [Export("sceneDidEnterBackground:")]
    public void DidEnterBackground(UIScene scene) => BackgroundRefreshLifecycleHook.Schedule();

    /// <summary>Returning to the foreground refreshes the relay registration (coalesced with any pass already queued).</summary>
    [Export("sceneWillEnterForeground:")]
    public void WillEnterForeground(UIScene scene) => BackgroundRefreshLifecycleHook.RequestForegroundRelaySync();

    private static void Route(IServiceProvider services, NSSet<UIOpenUrlContext>? contexts)
    {
        var urls = contexts?.ToArray().Select(context => context.Url).ToArray() ?? [];
        if (urls.Length == 0) return;
        _ = RouteAsync(services.GetRequiredService<UIKitActivationCoordinator>(), urls);
    }

    private static async Task RouteAsync(UIKitActivationCoordinator coordinator, NSUrl[] urls)
    {
        try { await coordinator.HandleAsync(urls); }
        catch (Exception error) { Serilog.Log.Error(error, "Activation handling failed."); }
    }
}
