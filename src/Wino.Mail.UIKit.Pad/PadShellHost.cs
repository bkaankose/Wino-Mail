using CoreGraphics;
using Foundation;
using Microsoft.Extensions.DependencyInjection;
using UIKit;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Mail.UIKit.Pad.Shell;

namespace Wino.Mail.UIKit.Pad;

/// <summary>
/// The iPad presentation surface: the split-view shell, rendering-frame pages in the reader column, pushes on the list
/// column (or the open sheet), form sheets, popovers anchored to a view (Move, filters, account switcher, search
/// suggestions) and pop-out window scenes for the reader and the composer.
/// </summary>
public sealed class PadShellHost(IServiceProvider services) : UIKitShellHostBase
{
    /// <summary>The user activity type of a pop-out window scene (reader or composer).</summary>
    public const string PopOutActivityType = "com.winomail.ios.popout";
    private const string PopOutKey = "popout";

    private readonly Dictionary<string, UIViewController> _pendingPopOuts = new(StringComparer.Ordinal);
    private readonly List<(UIWindowScene Scene, UIWindow Window, UIViewController Page)> _popOuts = [];
    // The popover presentation controller keeps its delegate weakly; the host keeps the last one alive.
    private PopoverDelegate? _popoverDelegate;

    public override UIKitShellViewController CreateShell() => services.GetRequiredService<PadShellViewController>();

    public override bool ShowInRenderingFrame(UIKitShellViewController shell, UIViewController controller)
        => shell is PadShellViewController pad && pad.ShowInReader(controller);

    protected override UIModalPresentationStyle SheetStyle => UIModalPresentationStyle.FormSheet;

    #region Popovers

    /// <summary>
    /// Presents <paramref name="content"/> as a popover anchored to <paramref name="anchor"/> (Move, filters, the account
    /// switcher). In a compact width the system adapts it to a sheet. <paramref name="dismissed"/> runs when the user
    /// dismisses it by tapping outside.
    /// </summary>
    public bool PresentPopover(UIViewController content, UIView anchor, CGSize preferredSize, Action? dismissed = null,
        UIPopoverArrowDirection arrows = UIPopoverArrowDirection.Any, CGRect? sourceRect = null)
    {
        if (anchor.Window is null || TopViewController is not { } presenter) return false;

        content.ModalPresentationStyle = UIModalPresentationStyle.Popover;
        content.PreferredContentSize = preferredSize;
        if (content.PopoverPresentationController is { } popover)
        {
            popover.SourceView = anchor;
            popover.SourceRect = sourceRect ?? anchor.Bounds;
            popover.PermittedArrowDirections = arrows;
            _popoverDelegate = new PopoverDelegate(dismissed);
            popover.Delegate = _popoverDelegate;
        }
        presenter.PresentViewController(content, true, null);
        return true;
    }

    /// <summary>Dismisses <paramref name="content"/> when it is the presented popover.</summary>
    public static void DismissPopover(UIViewController content)
    {
        if (content.PresentingViewController is { } presenting && !content.IsBeingDismissed) presenting.DismissViewController(true, null);
    }

    private sealed class PopoverDelegate(Action? dismissed) : UIPopoverPresentationControllerDelegate
    {
        public override void DidDismiss(UIPresentationController presentationController) => dismissed?.Invoke();
    }

    #endregion

    #region Pop-out window scenes

    /// <summary>True when the app can open a second window scene (iPad with UIApplicationSupportsMultipleScenes).</summary>
    public bool CanOpenPopOut => UIApplication.SharedApplication.SupportsMultipleScenes;

    /// <summary>
    /// Asks the system for a new window scene that shows <paramref name="page"/>. The page stays active; the scene
    /// delegate hands the connecting scene to <see cref="TryConnectPopOut"/>. False when multiple scenes are off.
    /// </summary>
    public bool RequestPopOut(UIViewController page)
    {
        if (!CanOpenPopOut) return false;

        var id = Guid.NewGuid().ToString("N");
        _pendingPopOuts[id] = page;
        var activity = new NSUserActivity(PopOutActivityType)
        {
            UserInfo = NSDictionary.FromObjectAndKey(new NSString(id), new NSString(PopOutKey))
        };
        var request = UISceneSessionActivationRequest.Create();
        request.UserActivity = activity;
        UIApplication.SharedApplication.ActivateSceneSession(request, error =>
        {
            Serilog.Log.Warning("The pop-out window could not open: {Error}", error?.LocalizedDescription);
            if (_pendingPopOuts.Remove(id, out var orphan)) ReleaseQuietly(orphan);
        });
        return true;
    }

    /// <summary>
    /// The scene delegate calls this first for every connecting scene. True when the scene is a pop-out window: the host
    /// gave it its window and page, and the delegate must not attach it as the main window.
    /// </summary>
    public bool TryConnectPopOut(UIWindowScene scene, UISceneConnectionOptions options)
    {
        var activity = options.UserActivities?.ToArray().FirstOrDefault(item => item.ActivityType == PopOutActivityType);
        if (activity?.UserInfo?[PopOutKey] is not NSString id || !_pendingPopOuts.Remove(id.ToString(), out var page)) return false;

        // Send, Cancel and Discard close the pop-out window (its page is released when the scene disconnects).
        if (page is Views.Mail.ComposePageViewController compose)
        {
            compose.CloseHandler = () =>
            {
                UIApplication.SharedApplication.RequestSceneSessionDestruction(scene.Session, null,
                    error => Serilog.Log.Warning("The pop-out window could not close: {Error}", error?.LocalizedDescription));
                return Task.CompletedTask;
            };
        }
        // The page left the reader column with its Auto Layout pins; the navigation controller sizes its root by frame.
        if (page.ViewIfLoaded is { } pageView)
        {
            pageView.TranslatesAutoresizingMaskIntoConstraints = true;
            pageView.AutoresizingMask = UIViewAutoresizing.FlexibleDimensions;
        }
        var navigation = new UINavigationController(page);
        if (navigation.View is { } navigationView) navigationView.BackgroundColor = UIColor.SystemBackground;
        var window = new UIWindow(scene) { RootViewController = navigation };
        UIKitThemeService.ApplyTo(window);
        window.MakeKeyAndVisible();
        _popOuts.Add((scene, window, page));
        return true;
    }

    /// <summary>The scene delegate calls this when a scene disconnects; a pop-out releases its page (drafts save).</summary>
    public void SceneDisconnected(UIScene scene)
    {
        var index = _popOuts.FindIndex(entry => ReferenceEquals(entry.Scene, scene));
        if (index < 0) return;

        var (_, window, page) = _popOuts[index];
        _popOuts.RemoveAt(index);
        window.RootViewController = null;
        ReleaseQuietly(page);
    }

    private static async void ReleaseQuietly(UIViewController page)
    {
        try { if (page is IWinoViewController routed) await routed.ReleaseAsync(); }
        catch (Exception exception) { Serilog.Log.Error(exception, "A pop-out page could not be released."); }
    }

    #endregion
}
