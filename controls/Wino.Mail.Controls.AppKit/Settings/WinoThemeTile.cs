using AppKit;
using CoreGraphics;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Settings;

/// <summary>
/// A Wino theme tile for the Personalization grid (design board SettingsPersonalization): the real
/// wallpaper or gradient at 150x92 with a miniature sidebar strip and content pane over it, an
/// accent stroke and check badge while selected, and the theme name underneath.
/// </summary>
public sealed class WinoThemeTile : NSView
{
    public const double TileWidth = 150;
    public const double PreviewHeight = 92;

    private readonly PreviewView _preview;
    private readonly WinoSurfaceView _badge;
    private readonly NSTextField _name;
    private bool _isSelected;

    public WinoThemeTile(string name, NSImage? wallpaper, uint[]? gradient, bool isDark)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _preview = new PreviewView(wallpaper, gradient, isDark) { CornerRadius = 8, Stroke = WinoSettingsStyle.CardStroke };
        WinoLayout.Size(_preview, TileWidth, PreviewHeight);

        _badge = new WinoSurfaceView { CornerRadius = 9, Fill = WinoStyle.Accent, Hidden = true };
        WinoLayout.Size(_badge, 18, 18);
        var check = new WinoIconView(WinoIconGlyph.Checkmark, 11, NSColor.White) { Colorful = false };
        _badge.AddSubview(check);
        _preview.AddSubview(_badge);
        NSLayoutConstraint.ActivateConstraints(
        [
            check.CenterXAnchor.ConstraintEqualTo(_badge.CenterXAnchor),
            check.CenterYAnchor.ConstraintEqualTo(_badge.CenterYAnchor),
            _badge.TopAnchor.ConstraintEqualTo(_preview.TopAnchor, 6),
            _badge.TrailingAnchor.ConstraintEqualTo(_preview.TrailingAnchor, -6)
        ]);

        _name = WinoStyle.Label(name, WinoSettingsStyle.CardDescription, WinoStyle.PrimaryText);
        var stack = WinoLayout.VStack(6, _preview, _name);
        stack.Alignment = NSLayoutAttribute.Leading;
        WinoLayout.Fill(stack, this);
        WidthAnchor.ConstraintEqualTo((nfloat)TileWidth).Active = true;

        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.RadioButtonRole;
        AccessibilityLabel = name;
        ApplySelection();
    }

    public event EventHandler? Pressed;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            AccessibilityValue = new Foundation.NSNumber(value ? 1 : 0);
            ApplySelection();
        }
    }

    public override bool AcceptsFirstMouse(NSEvent? theEvent) => true;

    public override void MouseUp(NSEvent theEvent)
    {
        if (Bounds.Contains(ConvertPointFromView(theEvent.LocationInWindow, null))) Pressed?.Invoke(this, EventArgs.Empty);
    }

    public override bool AccessibilityPerformPress()
    {
        Pressed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void ApplySelection()
    {
        _preview.Stroke = _isSelected ? WinoStyle.Accent : WinoSettingsStyle.CardStroke;
        _preview.StrokeWidth = _isSelected ? 2 : 1;
        _badge.Fill = WinoStyle.Accent;
        _badge.Hidden = !_isSelected;
        _name.Font = _isSelected ? WinoStyle.CaptionStrong : WinoSettingsStyle.CardDescription;
    }

    /// <summary>Draws the wallpaper aspect-filled (or the gradient) with the window miniature on top.</summary>
    private sealed class PreviewView(NSImage? wallpaper, uint[]? gradient, bool isDark) : WinoSurfaceView
    {
        public override bool WantsUpdateLayer => false;

        public override void DrawRect(CGRect dirtyRect)
        {
            var bounds = Bounds;
            var clip = NSBezierPath.FromRoundedRect(bounds, (nfloat)CornerRadius, (nfloat)CornerRadius);
            NSGraphicsContext.CurrentContext?.SaveGraphicsState();
            clip.AddClip();

            bool dark = isDark || (gradient is null && wallpaper is null && WinoIcons.IsDark(EffectiveAppearance));
            if (wallpaper is { } image && image.Size.Width > 0 && image.Size.Height > 0)
            {
                var scale = Math.Max(bounds.Width / image.Size.Width, bounds.Height / image.Size.Height);
                var size = new CGSize(image.Size.Width * scale, image.Size.Height * scale);
                var target = new CGRect((bounds.Width - size.Width) / 2, (bounds.Height - size.Height) / 2, size.Width, size.Height);
                image.Draw(target, CGRect.Empty, NSCompositingOperation.SourceOver, 1, true, null);
            }
            else if (gradient is { Length: 4 } stops)
            {
                dark = WinoIcons.IsDark(EffectiveAppearance);
                using var fill = new NSGradient(WinoStyle.Hex(dark ? stops[2] : stops[0]), WinoStyle.Hex(dark ? stops[3] : stops[1]));
                fill.DrawInRect(bounds, -72);
            }
            else
            {
                NSColor.WindowBackground.SetFill();
                NSGraphics.RectFill(bounds);
            }

            // Miniature window: sidebar strip and content pane (board: 26pt strip at 10/12, pane 42..10).
            // A plain (Default) background needs tinted shapes; wallpapers take translucent white ones.
            bool plain = wallpaper is null && gradient is null;
            var pane = plain
                ? (dark ? WinoStyle.Hex(0xFFFFFF, 0.08) : WinoStyle.Hex(0x000000, 0.05))
                : dark ? WinoStyle.Hex(0x1E1E22, 0.85) : WinoStyle.Hex(0xFFFFFF, 0.88);
            var strip = plain
                ? (dark ? WinoStyle.Hex(0xFFFFFF, 0.12) : WinoStyle.Hex(0x000000, 0.08))
                : dark ? WinoStyle.Hex(0xFFFFFF, 0.18) : WinoStyle.Hex(0xFFFFFF, 0.35);
            var top = bounds.Height - 12;
            strip.SetFill();
            NSBezierPath.FromRoundedRect(new CGRect(10, 10, 26, top - 10), 3, 3).Fill();
            pane.SetFill();
            NSBezierPath.FromRoundedRect(new CGRect(42, 10, bounds.Width - 52, top - 10), 4, 4).Fill();
            NSGraphicsContext.CurrentContext?.RestoreGraphicsState();

            if (Stroke is { } stroke)
            {
                var inset = (nfloat)(StrokeWidth / 2);
                var border = NSBezierPath.FromRoundedRect(bounds.Inset(inset, inset), (nfloat)CornerRadius - inset, (nfloat)CornerRadius - inset);
                border.LineWidth = (nfloat)StrokeWidth;
                stroke.SetStroke();
                border.Stroke();
            }
        }

        public override void ViewDidChangeEffectiveAppearance()
        {
            base.ViewDidChangeEffectiveAppearance();
            NeedsDisplay = true;
        }
    }
}
