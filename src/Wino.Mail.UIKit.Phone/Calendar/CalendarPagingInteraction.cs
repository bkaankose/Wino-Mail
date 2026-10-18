using CoreGraphics;
using UIKit;

namespace Wino.Mail.UIKit.Phone.Calendar;

/// <summary>
/// Pages the iPhone calendar body with the finger (design pass of 18 Oct 2026): a mostly horizontal drag moves a snapshot
/// of the current period with the touch; past 30% of the width (or a quick flick) the snapshot leaves, the page moves to
/// the next or previous period and the live content slides in from the other side; a short drag springs back. Reduce
/// Motion crossfades.
/// The pan does not start while the surface drags a tile or selects a time range.
/// </summary>
internal sealed class CalendarPagingInteraction
{
    private const double CommitFraction = 0.3;
    private const double CommitVelocity = 400;
    private const double Duration = 0.28;

    private readonly UIView _body;
    private readonly Func<bool> _canPage;
    private readonly Action<int> _move;
    private UIView? _snapshot;

    /// <param name="body">The view that holds the surface and the agenda.</param>
    /// <param name="canPage">False while another gesture owns the body (a tile drag, a range selection).</param>
    /// <param name="move">Moves the period: +1 next, -1 previous.</param>
    public CalendarPagingInteraction(UIView body, Func<bool> canPage, Action<int> move)
    {
        _body = body;
        _canPage = canPage;
        _move = move;
        var pan = new UIPanGestureRecognizer(Panned) { CancelsTouchesInView = true };
        pan.ShouldBegin = recognizer =>
        {
            if (!_canPage()) return false;
            var velocity = ((UIPanGestureRecognizer)recognizer).VelocityInView(body);
            return Math.Abs(velocity.X) > Math.Abs(velocity.Y) * 1.5;
        };
        body.AddGestureRecognizer(pan);
        Pan = pan;
    }

    public UIPanGestureRecognizer Pan { get; }

    private void Panned(UIPanGestureRecognizer recognizer)
    {
        var width = (double)_body.Bounds.Width;
        var dx = (double)recognizer.TranslationInView(_body).X;
        switch (recognizer.State)
        {
            case UIGestureRecognizerState.Began:
                TakeSnapshot();
                break;
            case UIGestureRecognizerState.Changed:
                if (_snapshot is { } snapshot) snapshot.Transform = CGAffineTransform.MakeTranslation((nfloat)dx, 0);
                break;
            case UIGestureRecognizerState.Ended:
                var velocity = (double)recognizer.VelocityInView(_body).X;
                bool commit = Math.Abs(dx) > width * CommitFraction || Math.Abs(velocity) > CommitVelocity && Math.Sign(velocity) == Math.Sign(dx);
                if (commit && dx != 0) Commit(dx < 0 ? 1 : -1, dx);
                else Cancel();
                break;
            case UIGestureRecognizerState.Cancelled:
            case UIGestureRecognizerState.Failed:
                Cancel();
                break;
        }
    }

    /// <summary>Covers the body with a picture of the current period and hides the live content under it.</summary>
    private void TakeSnapshot()
    {
        RemoveSnapshot();
        if (_body.Superview is not { } host || _body.SnapshotView(false) is not { } snapshot) return;
        snapshot.Frame = _body.Frame;
        snapshot.UserInteractionEnabled = false;
        host.InsertSubviewAbove(snapshot, _body);
        _snapshot = snapshot;
        _body.Alpha = 0;
    }

    private void Commit(int direction, double dx)
    {
        var width = (nfloat)(double)_body.Bounds.Width;
        _move(direction);
        if (UIAccessibility.IsReduceMotionEnabled)
        {
            UIView.Animate(0.15, () =>
            {
                if (_snapshot is { } snapshot) snapshot.Alpha = 0;
                _body.Alpha = 1;
            }, RemoveSnapshot);
            return;
        }
        // The new period comes in from the side the finger moved away from.
        _body.Transform = CGAffineTransform.MakeTranslation(direction > 0 ? width : -width, 0);
        _body.Alpha = 1;
        UIView.Animate(Duration, 0, UIViewAnimationOptions.CurveEaseOut | UIViewAnimationOptions.AllowUserInteraction, () =>
        {
            if (_snapshot is { } snapshot) snapshot.Transform = CGAffineTransform.MakeTranslation(direction > 0 ? -width : width, 0);
            _body.Transform = CGAffineTransform.MakeIdentity();
        }, RemoveSnapshot);
    }

    private void Cancel()
    {
        if (_snapshot is not { } snapshot)
        {
            _body.Alpha = 1;
            return;
        }
        UIView.AnimateNotify(0.3, 0, 0.85f, 0, UIViewAnimationOptions.AllowUserInteraction,
            () => snapshot.Transform = CGAffineTransform.MakeIdentity(),
            _ => RemoveSnapshot());
    }

    private void RemoveSnapshot()
    {
        _snapshot?.RemoveFromSuperview();
        _snapshot = null;
        _body.Alpha = 1;
        _body.Transform = CGAffineTransform.MakeIdentity();
    }
}
