using Android.Graphics;
using Android.Views;
using Wino.Presentation.Android;

namespace Wino.Mail.Controls.Android.Common;

/// <summary>
/// Grows small controls inside a row (a 40 dp avatar, an 11 dp thread chevron) to the 48 dp minimum touch target
/// without changing how they look. A view takes one <see cref="TouchDelegate"/>, so this one forwards to one delegate per
/// control. The host calls <see cref="Update"/> after layout.
/// </summary>
public sealed class WinoTouchTargets : TouchDelegate
{
    private const int MinimumDp = 48;
    private readonly ViewGroup _host;
    private readonly View[] _targets;
    private readonly List<TouchDelegate> _delegates = [];

    public WinoTouchTargets(ViewGroup host, params View[] targets) : base(new Rect(), host)
    {
        _host = host;
        _targets = targets;
    }

    /// <summary>Recomputes the touch areas from the controls' current bounds; call from the host's OnLayout.</summary>
    public void Update()
    {
        _delegates.Clear();
        if (_host.Context is not { } context) return;
        int minimum = WinoUnits.Dp(context, MinimumDp);
        foreach (var target in _targets)
        {
            if (target.Visibility != ViewStates.Visible || !target.Clickable || target.Width == 0) continue;
            var bounds = new Rect(0, 0, target.Width, target.Height);
            _host.OffsetDescendantRectToMyCoords(target, bounds);
            int growX = Math.Max(0, (minimum - bounds.Width()) / 2);
            int growY = Math.Max(0, (minimum - bounds.Height()) / 2);
            bounds.Inset(-growX, -growY);
            _delegates.Add(new TouchDelegate(bounds, target));
        }
    }

    public override bool OnTouchEvent(MotionEvent e)
    {
        foreach (var target in _delegates)
            if (target.OnTouchEvent(e)) return true;
        return false;
    }
}
