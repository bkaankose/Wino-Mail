using AppKit;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Extras;

/// <summary>
/// A centred empty/unavailable/error state (Windows: 26pt WinoFontIcon, BodyStrong title,
/// wrapped caption, optional button; spacing 8, margin 24). Add it to a container with
/// <see cref="Place"/> and toggle <see cref="NSView.Hidden"/>.
/// </summary>
public sealed class WinoStateView : NSView
{
    private readonly NSStackView _stack;

    public WinoStateView(WinoIconGlyph glyph, string title, string? caption, NSColor? tint = null)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Icon = new WinoIconView(glyph, 26, tint);
        Title = WinoStyle.Label(title, WinoStyle.BodyStrong, WinoStyle.PrimaryText, 0);
        Title.Alignment = NSTextAlignment.Center;
        Caption = WinoStyle.Label(caption, WinoStyle.Caption, WinoStyle.SecondaryText, 0);
        Caption.Alignment = NSTextAlignment.Center;
        Caption.Hidden = string.IsNullOrEmpty(caption);
        _stack = WinoLayout.VStack(8, Icon, Title, Caption);
        _stack.Alignment = NSLayoutAttribute.CenterX;
        AddSubview(_stack);
        NSLayoutConstraint.ActivateConstraints(
        [
            _stack.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            _stack.CenterYAnchor.ConstraintEqualTo(CenterYAnchor),
            _stack.LeadingAnchor.ConstraintGreaterThanOrEqualTo(LeadingAnchor, 24),
            _stack.TrailingAnchor.ConstraintLessThanOrEqualTo(TrailingAnchor, -24),
            _stack.WidthAnchor.ConstraintLessThanOrEqualTo(320)
        ]);
    }

    public WinoIconView Icon { get; }
    public NSTextField Title { get; }
    public NSTextField Caption { get; }

    /// <summary>Adds a control under the caption (Retry).</summary>
    public void Add(NSView view) => _stack.AddArrangedSubview(view);

    /// <summary>Replaces the icon with a spinner, for loading states.</summary>
    public NSProgressIndicator UseSpinner(double size = 32)
    {
        var spinner = new NSProgressIndicator
        {
            Style = NSProgressIndicatorStyle.Spinning,
            Indeterminate = true,
            ControlSize = NSControlSize.Regular,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoLayout.Size(spinner, size, size);
        _stack.InsertArrangedSubview(spinner, 0);
        Icon.RemoveFromSuperview();
        return spinner;
    }

    public void Place(NSView parent) => WinoLayout.Fill(this, parent);
}
