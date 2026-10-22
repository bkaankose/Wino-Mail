using UIKit;
using Wino.Core.Domain;
using Wino.Editor;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Mail;

/// <summary>
/// The address of the link under the pointer over the bottom of the reader (Windows WinoMailRenderer link bar; the
/// macOS ReaderLinkTargetBar). Only a hovering pointer (trackpad or mouse on iPad) shows it: a finger tap also reports
/// its link through compatibility mouse events, so <see cref="Show"/> waits for the hover recognizer that
/// <see cref="Attach"/> adds to the web view. An Outlook Safe Links address shows where it leads. It never takes touches.
/// </summary>
internal sealed class ReaderLinkTargetBar : WinoSurfaceView
{
    private readonly UILabel _text = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);
    private bool _pointerHovering;

    public ReaderLinkTargetBar()
    {
        CornerRadius = 6;
        Fill = UIColor.SecondarySystemBackground;
        Stroke = WinoStyle.ZoneStroke;
        Hidden = true;
        UserInteractionEnabled = false;
        // The text is visual only; the link itself is announced by the reader document.
        IsAccessibilityElement = false;
        AccessibilityElementsHidden = true;
        _text.LineBreakMode = UILineBreakMode.TailTruncation;
        _text.SetContentCompressionResistancePriority(250, UILayoutConstraintAxis.Horizontal);
        WinoLayout.Fill(_text, this, 4, 10, 4, 10);
    }

    /// <summary>
    /// Pins the bar to the bottom-leading corner of the visible <paramref name="frame"/> (Windows margin 8,0,48,8; at
    /// most 720 wide), inside <paramref name="host"/>, and follows the pointer over <paramref name="web"/>.
    /// </summary>
    public void Attach(UIView host, UIView frame, UIView web)
    {
        host.AddSubview(this);
        NSLayoutConstraint.ActivateConstraints(
        [
            LeadingAnchor.ConstraintEqualTo(frame.LeadingAnchor, 8),
            BottomAnchor.ConstraintEqualTo(frame.BottomAnchor, -8),
            TrailingAnchor.ConstraintLessThanOrEqualTo(frame.TrailingAnchor, -48),
            WidthAnchor.ConstraintLessThanOrEqualTo(720)
        ]);
        web.AddGestureRecognizer(new UIHoverGestureRecognizer(recognizer =>
        {
            _pointerHovering = recognizer.State is UIGestureRecognizerState.Began or UIGestureRecognizerState.Changed;
            if (!_pointerHovering) Clear();
        }));
    }

    public void Show(RendererLinkHoverEventArgs args)
    {
        if (args.Uri is null || !_pointerHovering)
        {
            Clear();
            return;
        }

        _text.Text = args.SafeLinkTarget is { } target
            ? Translator.Reader_ProtectedLinkFormat.Replace("{0}", target, StringComparison.Ordinal)
            : args.Uri;
        Hidden = false;
    }

    public void Clear()
    {
        Hidden = true;
        _text.Text = string.Empty;
    }
}
