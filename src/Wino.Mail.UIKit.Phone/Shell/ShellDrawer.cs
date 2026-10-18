using CoreGraphics;
using UIKit;
using Wino.Core.Domain;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Phone.Shell;

/// <summary>
/// The iPhone shell drawer (iOS design decisions, revision 18 Oct): a blurred panel that slides in from the left over
/// the active tab's stack and the tab bar, min(85% of the width, 340pt) wide and full height, with a 34% black scrim over
/// the rest. Every tab opens it from the hamburger bar button or a left-edge pan; it closes on a scrim tap, a leftward
/// swipe or a row pick. The panel hosts the active mode's menu (Mailboxes, the calendars, the contact filters, the To Do
/// lists, each with the Settings row at its foot) in its own navigation controller as a child of the shell; the shell
/// swaps that root when the mode changes. Reduce Motion fades the panel instead of sliding it.
/// </summary>
internal sealed class ShellDrawer
{
    private const double MaximumWidth = 340;
    private const double WidthFraction = 0.85;
    private const double ScrimAlpha = 0.34;
    private const double Duration = 0.3;

    private readonly UIViewController _host;
    private readonly UINavigationController _content;
    private readonly Func<bool> _canOpen;
    private readonly UIView _scrim = new() { BackgroundColor = UIColor.Black.ColorWithAlpha((nfloat)ScrimAlpha), Alpha = 0, Hidden = true };
    private readonly DrawerPanel _panel = new() { Hidden = true };
    private readonly UIVisualEffectView _blur = new(UIBlurEffect.FromStyle(UIBlurEffectStyle.SystemMaterial));
    private double _progress;
    private bool _attached;
    private int _animations;

    public ShellDrawer(UIViewController host, UIViewController root, Func<bool> canOpen)
    {
        _host = host;
        _canOpen = canOpen;
        _content = new UINavigationController(root);
        _content.NavigationBar.PrefersLargeTitles = false;
        _panel.Escape = () => Close(true);
    }

    /// <summary>True while the drawer is open or opening.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>The menu the panel shows (the active mode's); the drawer closes first when it is open.</summary>
    public void SetRoot(UIViewController root)
    {
        if (_content.ViewControllers is [var current] && ReferenceEquals(current, root)) return;
        Close(false);
        _content.SetViewControllers([root], false);
    }

    /// <summary>Adds the scrim, the panel and the edge gesture to the shell's root view (above the tab bar controller).</summary>
    public void Attach(UIView root)
    {
        if (_attached) return;
        _attached = true;

        _scrim.IsAccessibilityElement = true;
        _scrim.AccessibilityLabel = Translator.Buttons_Close;
        _scrim.AccessibilityTraits = UIAccessibilityTrait.Button;
        _scrim.AddGestureRecognizer(new UITapGestureRecognizer(() => Close(true)));
        var scrimPan = new UIPanGestureRecognizer(PanToClose);
        _scrim.AddGestureRecognizer(scrimPan);
        root.AddSubview(_scrim);

        _panel.ClipsToBounds = false;
        _panel.Layer.ShadowColor = UIColor.Black.CGColor;
        _panel.Layer.ShadowOpacity = 0.22f;
        _panel.Layer.ShadowRadius = 18;
        _panel.Layer.ShadowOffset = new CGSize(4, 0);
        _panel.AccessibilityViewIsModal = true;
        _blur.Frame = _panel.Bounds;
        _blur.AutoresizingMask = UIViewAutoresizing.FlexibleDimensions;
        _panel.AddSubview(_blur);

        _host.AddChildViewController(_content);
        if (_content.View is { } contentView)
        {
            contentView.BackgroundColor = UIColor.Clear;
            contentView.Frame = _panel.Bounds;
            contentView.AutoresizingMask = UIViewAutoresizing.FlexibleDimensions;
            _panel.AddSubview(contentView);
        }
        _content.DidMoveToParentViewController(_host);

        var panelPan = new UIPanGestureRecognizer(PanToClose);
        // Only a mostly horizontal, leftward drag closes the drawer; vertical drags scroll the folders.
        panelPan.ShouldBegin = recognizer =>
        {
            var velocity = ((UIPanGestureRecognizer)recognizer).VelocityInView(_panel);
            return velocity.X < 0 && Math.Abs(velocity.X) > Math.Abs(velocity.Y) * 1.2;
        };
        _panel.AddGestureRecognizer(panelPan);
        root.AddSubview(_panel);

        // A thin strip on the left edge above the content takes the opening drag, so the list's leading swipe action
        // (Archive) and scroll pan never see it. While the drawer cannot open the strip lets touches through, so a
        // pushed reader keeps its back swipe.
        var edge = new EdgeStrip(() => !IsOpen && _canOpen()) { TranslatesAutoresizingMaskIntoConstraints = false };
        edge.AddGestureRecognizer(new UIPanGestureRecognizer(PanToOpen));
        root.InsertSubviewBelow(edge, _scrim);
        NSLayoutConstraint.ActivateConstraints(
        [
            edge.LeadingAnchor.ConstraintEqualTo(root.LeadingAnchor),
            edge.TopAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.TopAnchor),
            edge.BottomAnchor.ConstraintEqualTo(root.SafeAreaLayoutGuide.BottomAnchor),
            edge.WidthAnchor.ConstraintEqualTo((nfloat)EdgeStrip.Width)
        ]);
    }

    private nfloat Width(CGRect bounds) => (nfloat)Math.Min(MaximumWidth, (double)bounds.Width * WidthFraction);

    /// <summary>Places the scrim and panel for the shell's bounds and the current progress.</summary>
    public void Layout(CGRect bounds)
    {
        if (!_attached) return;
        _scrim.Frame = bounds;
        var width = Width(bounds);
        bool slides = !UIAccessibility.IsReduceMotionEnabled;
        var x = slides ? -width * (nfloat)(1 - _progress) : 0;
        _panel.Frame = new CGRect(x, 0, width, bounds.Height);
        _panel.Layer.ShadowPath = UIBezierPath.FromRect(_panel.Bounds).CGPath;
        _panel.Alpha = slides ? 1 : (nfloat)_progress;
        _scrim.Alpha = (nfloat)_progress;
        // While a close animates the panel stays visible; the completion hides it.
        bool visible = _progress > 0 || _animations > 0;
        _panel.Hidden = !visible;
        _scrim.Hidden = !visible;
        if (_panel.Superview is { } root)
        {
            root.BringSubviewToFront(_scrim);
            root.BringSubviewToFront(_panel);
        }
    }

    public void Open(bool animated)
    {
        if (!_attached || IsOpen) return;
        IsOpen = true;
        _host.View?.EndEditing(true);
        _content.LoadViewIfNeeded();
        SetProgress(1, animated, null);
        UIAccessibility.PostNotification(UIAccessibilityPostNotification.ScreenChanged, _panel);
    }

    public void Close(bool animated, Action? completion = null)
    {
        if (!_attached || (!IsOpen && _progress <= 0))
        {
            completion?.Invoke();
            return;
        }
        IsOpen = false;
        SetProgress(0, animated, () =>
        {
            UIAccessibility.PostNotification(UIAccessibilityPostNotification.ScreenChanged, null);
            completion?.Invoke();
        });
    }

    private void SetProgress(double progress, bool animated, Action? completion)
    {
        var bounds = _host.View?.Bounds ?? CGRect.Empty;
        if (_progress <= 0 && progress > 0) PrepareFirstLayout(bounds);
        _progress = progress;
        if (!animated)
        {
            Layout(bounds);
            completion?.Invoke();
            return;
        }
        _animations++;
        var reduceMotion = UIAccessibility.IsReduceMotionEnabled;
        UIView.AnimateNotify(reduceMotion ? 0.2 : Duration, 0, reduceMotion ? 1 : (nfloat)0.9, 0, UIViewAnimationOptions.AllowUserInteraction | UIViewAnimationOptions.BeginFromCurrentState,
            () => Layout(bounds),
            _ =>
            {
                _animations--;
                Layout(_host.View?.Bounds ?? bounds);
                completion?.Invoke();
            });
    }

    /// <summary>
    /// Lays the panel out at its closed position without animation before it first slides in, so its content does not
    /// grow from a zero frame inside the animation block.
    /// </summary>
    private void PrepareFirstLayout(CGRect bounds)
    {
        UIView.PerformWithoutAnimation(() =>
        {
            Layout(bounds);
            _panel.Hidden = false;
            _panel.LayoutIfNeeded();
        });
    }

    // ---- Interactive gestures ----

    private void PanToOpen(UIPanGestureRecognizer recognizer) => Track(recognizer, opening: true);

    private void PanToClose(UIPanGestureRecognizer recognizer) => Track(recognizer, opening: false);

    private void Track(UIPanGestureRecognizer recognizer, bool opening)
    {
        if (_host.View is not { } root) return;
        var width = (double)Width(root.Bounds);
        var translation = (double)recognizer.TranslationInView(root).X;
        switch (recognizer.State)
        {
            case UIGestureRecognizerState.Began:
                if (opening)
                {
                    root.EndEditing(true);
                    _content.LoadViewIfNeeded();
                    PrepareFirstLayout(root.Bounds);
                }
                break;
            case UIGestureRecognizerState.Changed:
                var progress = opening ? translation / width : 1 + translation / width;
                _progress = Math.Clamp(progress, 0.001, 1);
                Layout(root.Bounds);
                break;
            case UIGestureRecognizerState.Ended:
            case UIGestureRecognizerState.Cancelled:
            case UIGestureRecognizerState.Failed:
                var velocity = (double)recognizer.VelocityInView(root).X;
                bool open = recognizer.State == UIGestureRecognizerState.Ended && (Math.Abs(velocity) > 300 ? velocity > 0 : _progress > 0.5);
                if (open)
                {
                    IsOpen = false;
                    Open(true);
                }
                else
                {
                    IsOpen = true;
                    Close(true);
                }
                break;
        }
    }

    /// <summary>The panel: the VoiceOver escape gesture (two-finger Z) closes the drawer.</summary>
    private sealed class DrawerPanel : UIView
    {
        public Action? Escape { get; set; }

        public override bool AccessibilityPerformEscape()
        {
            Escape?.Invoke();
            return Escape is not null;
        }
    }

    /// <summary>The invisible opening strip: it only claims touches while the drawer can open.</summary>
    private sealed class EdgeStrip(Func<bool> active) : UIView
    {
        public const double Width = 20;

        public override bool PointInside(CGPoint point, UIEvent? uievent) => active() && base.PointInside(point, uievent);
    }
}
