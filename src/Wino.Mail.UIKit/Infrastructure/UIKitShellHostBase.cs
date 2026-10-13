using UIKit;

namespace Wino.Mail.UIKit.Infrastructure;

/// <summary>
/// The parts of <see cref="IUIKitShellHost"/> both idioms share: the scene window and its root, navigation sheets
/// (with the swipe-to-dismiss callback), the top-most presenter and pushes on an open sheet. The idiom hosts add
/// the shell and its rendering frame.
/// </summary>
public abstract class UIKitShellHostBase : IUIKitShellHost
{
    private readonly TaskCompletionSource<UIWindow> _windowReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // The presentation controller keeps its delegate weakly; the host keeps each sheet's delegate alive.
    private readonly List<(UINavigationController Sheet, SheetDismissal Delegate)> _sheets = [];

    public UIWindow? Window { get; private set; }

    public Task<UIWindow> WindowReady => _windowReady.Task;

    public void AttachWindow(UIWindow window)
    {
        Window = window;
        _windowReady.TrySetResult(window);
    }

    public virtual void ShowRoot(UIViewController controller)
    {
        var window = Window ?? throw new InvalidOperationException("The scene window is not attached yet.");
        var previous = window.RootViewController;
        if (previous is null)
        {
            window.RootViewController = controller;
            window.MakeKeyAndVisible();
            return;
        }
        // A sheet left on the outgoing root (add account, Settings) goes with it.
        if (previous.PresentedViewController is not null) previous.DismissViewController(false, null);
        _sheets.Clear();
        UIView.Transition(window, 0.25, UIViewAnimationOptions.TransitionCrossDissolve, () => window.RootViewController = controller, () => { });
    }

    public abstract UIKitShellViewController CreateShell();

    public abstract bool ShowInRenderingFrame(UIKitShellViewController shell, UIViewController controller);

    public virtual bool Push(UIKitShellViewController? shell, UIViewController controller)
    {
        var stack = OpenSheet ?? shell?.CurrentNavigationController;
        if (stack is null) return false;
        stack.PushViewController(controller, true);
        return true;
    }

    /// <summary>The top-most sheet presented by this host, if any.</summary>
    protected UINavigationController? OpenSheet => _sheets.Count == 0 ? null : _sheets[^1].Sheet;

    public UINavigationController PresentSheet(UIViewController controller, UIKitPageHost style, Action dismissedByUser)
    {
        var presenter = TopViewController ?? throw new InvalidOperationException("Nothing is on screen to present a sheet from.");
        var sheet = new UINavigationController(controller)
        {
            ModalPresentationStyle = style == UIKitPageHost.FullScreenSheet ? UIModalPresentationStyle.FullScreen : SheetStyle,
        };
        // A full-screen navigation controller is black behind pages with a clear root (the composer's zone gutter).
        if (style == UIKitPageHost.FullScreenSheet && sheet.View is { } sheetView) sheetView.BackgroundColor = UIColor.SystemGroupedBackground;
        var dismissal = new SheetDismissal(() =>
        {
            _sheets.RemoveAll(entry => ReferenceEquals(entry.Sheet, sheet));
            dismissedByUser();
        });
        if (sheet.PresentationController is { } presentation) presentation.Delegate = dismissal;
        _sheets.Add((sheet, dismissal));
        presenter.PresentViewController(sheet, true, null);
        return sheet;
    }

    /// <summary>The style of a <see cref="UIKitPageHost.Sheet"/>: a page sheet on iPhone, a form sheet on iPad.</summary>
    protected abstract UIModalPresentationStyle SheetStyle { get; }

    public Task DismissSheetAsync(UINavigationController sheet)
    {
        _sheets.RemoveAll(entry => ReferenceEquals(entry.Sheet, sheet));
        if (sheet.PresentingViewController is not { } presenting) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        presenting.DismissViewController(true, () => completion.TrySetResult());
        return completion.Task;
    }

    public virtual bool GoBack(UIKitShellViewController? shell)
    {
        var stack = OpenSheet ?? shell?.CurrentNavigationController;
        if (stack is null || stack.ViewControllers is not { Length: > 1 }) return false;
        stack.PopViewController(true);
        return true;
    }

    public UIViewController? TopViewController
    {
        get
        {
            var top = Window?.RootViewController;
            while (top?.PresentedViewController is { IsBeingDismissed: false } presented) top = presented;
            return top;
        }
    }

    /// <summary>Reports a sheet the user swiped away (presentationControllerDidDismiss).</summary>
    private sealed class SheetDismissal(Action dismissed) : UIAdaptivePresentationControllerDelegate
    {
        public override void DidDismiss(UIPresentationController presentationController) => dismissed();
    }
}
