using AppKit;
using CoreGraphics;
using Wino.Core.Domain;
using Wino.Editor;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Mail;

/// <summary>
/// The address of the hovered or focused link over the bottom of the reader (Windows WinoMailRenderer link bar, which
/// replaces the WebView2 status bar). Shown only while the reader reports a link; an Outlook Safe Links address shows
/// where it leads. It never takes the pointer, so the link under it stays hoverable and clickable.
/// </summary>
internal sealed class ReaderLinkTargetBar : WinoSurfaceView
{
    private readonly NSTextField _text = WinoStyle.Label(string.Empty, WinoStyle.Caption, WinoStyle.SecondaryText);

    public ReaderLinkTargetBar()
    {
        CornerRadius = 6;
        Fill = NSColor.WindowBackground;
        Stroke = WinoStyle.ZoneStroke;
        Hidden = true;
        // The text is visual only; the link itself is announced by the reader document.
        AccessibilityElement = false;
        _text.AccessibilityElement = false;
        _text.SetContentCompressionResistancePriority(250, NSLayoutConstraintOrientation.Horizontal);
        WinoLayout.Fill(_text, this, 4, 10, 4, 10);
    }

    /// <summary>Pins the bar to the bottom-leading corner of <paramref name="host"/> (Windows margin 8,0,48,8; at most 720 wide).</summary>
    public void Attach(NSView host)
    {
        host.AddSubview(this);
        NSLayoutConstraint.ActivateConstraints(
        [
            LeadingAnchor.ConstraintEqualTo(host.LeadingAnchor, 8),
            BottomAnchor.ConstraintEqualTo(host.BottomAnchor, -8),
            TrailingAnchor.ConstraintLessThanOrEqualTo(host.TrailingAnchor, -48),
            WidthAnchor.ConstraintLessThanOrEqualTo(720)
        ]);
    }

    public void Show(RendererLinkHoverEventArgs args)
    {
        if (args.Uri is null)
        {
            Clear();
            return;
        }

        _text.StringValue = args.SafeLinkTarget is { } target
            ? Translator.Reader_ProtectedLinkFormat.Replace("{0}", target, StringComparison.Ordinal)
            : args.Uri;
        Hidden = false;
    }

    public void Clear()
    {
        Hidden = true;
        _text.StringValue = string.Empty;
    }

    public override NSView? HitTest(CGPoint point) => null;
}
