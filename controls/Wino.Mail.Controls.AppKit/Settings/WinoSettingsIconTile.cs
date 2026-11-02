using AppKit;
using CoreAnimation;
using CoreGraphics;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Settings;

/// <summary>
/// The 30pt leading tile of a settings card: a white glyph on a top-lit gradient of its tint. Plain tiles
/// (nested expander rows) drop the gradient and draw a secondary glyph in the same column. Colours resolve
/// in <see cref="UpdateLayer"/>, so appearance changes need no observer.
/// </summary>
public sealed class WinoSettingsIconTile : NSView
{
    private static readonly NSColor Highlight = WinoStyle.Hex(0xFFFFFF, 0.18);

    private readonly CAGradientLayer _gradient = new() { CornerRadius = (nfloat)WinoSettingsStyle.IconTileRadius, MasksToBounds = true };
    private readonly WinoIconView _glyph;
    private WinoSettingsTint _tint;
    private bool _isPlain;
    private bool _isMuted;

    public WinoSettingsIconTile(WinoIconGlyph glyph = WinoIconGlyph.None, WinoSettingsTint? tint = null)
    {
        WinoLiveObjects.Track(this);
        TranslatesAutoresizingMaskIntoConstraints = false;
        WantsLayer = true;
        _tint = tint ?? WinoSettingsStyle.TintFor(glyph);
        _glyph = new WinoIconView(glyph, WinoSettingsStyle.IconGlyphSize, NSColor.White) { Colorful = false };
        WinoLayout.Size(_glyph, WinoSettingsStyle.IconGlyphSize, WinoSettingsStyle.IconGlyphSize);
        AddSubview(_glyph);
        WinoLayout.Size(this, WinoSettingsStyle.IconSize, WinoSettingsStyle.IconSize);
        NSLayoutConstraint.ActivateConstraints(
        [
            _glyph.CenterXAnchor.ConstraintEqualTo(CenterXAnchor),
            _glyph.CenterYAnchor.ConstraintEqualTo(CenterYAnchor)
        ]);
        AccessibilityElement = false;
    }

    public WinoIconGlyph Glyph
    {
        set
        {
            _glyph.Icon = value;
            _tint = WinoSettingsStyle.TintFor(value);
            NeedsDisplay = true;
        }
    }

    public WinoSettingsTint Tint
    {
        get => _tint;
        set { _tint = value; NeedsDisplay = true; }
    }

    /// <summary>No gradient, a secondary glyph: the icon column of nested rows.</summary>
    public bool IsPlain
    {
        get => _isPlain;
        set
        {
            _isPlain = value;
            _glyph.Tint = value ? WinoStyle.SecondaryText : NSColor.White;
            NeedsDisplay = true;
        }
    }

    /// <summary>Disabled cards fall back to the gray tint.</summary>
    public bool IsMuted
    {
        get => _isMuted;
        set { _isMuted = value; NeedsDisplay = true; }
    }

    public override bool WantsUpdateLayer => true;

    public override void UpdateLayer()
    {
        if (Layer is not { } layer) return;
        if (_gradient.SuperLayer is null) layer.InsertSublayer(_gradient, 0);
        _gradient.Hidden = _isPlain;
        if (_isPlain) return;
        var tint = WinoSettingsStyle.TintColor(_isMuted ? WinoSettingsTint.Gray : _tint).UsingColorSpace(NSColorSpace.SRGBColorSpace) ?? NSColor.Gray;
        var top = tint.BlendedColor(0.18f, NSColor.White) ?? tint;
        // Layer geometry is not flipped: the first colour is the bottom edge.
        _gradient.Colors = [tint.CGColor, top.CGColor];
        _gradient.BorderColor = Highlight.CGColor;
        _gradient.BorderWidth = 0.5f;
    }

    public override void Layout()
    {
        base.Layout();
        CATransaction.Begin();
        CATransaction.DisableActions = true;
        _gradient.Frame = Bounds;
        CATransaction.Commit();
    }
}
