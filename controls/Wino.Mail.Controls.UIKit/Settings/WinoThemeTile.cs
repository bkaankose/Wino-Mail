using CoreGraphics;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Mail.Controls.Core.Layout;
using Wino.Mail.Controls.UIKit.Extras;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Settings;

/// <summary>
/// A Wino theme tile for the Personalization grid (UIKit twin of the AppKit <c>WinoThemeTile</c>): the wallpaper or
/// gradient at 150×92 with a miniature sidebar strip and content pane over it, an accent stroke and check badge while
/// selected, and the theme name underneath.
/// </summary>
public sealed class WinoThemeTile : WinoPressableView
{
    public const double TileWidth = 150;
    public const double PreviewHeight = 92;

    private readonly PreviewView _preview;
    private readonly WinoSurfaceView _badge;
    private readonly UILabel _name;
    private bool _isSelected;

    public WinoThemeTile(string name, UIImage? wallpaper, uint[]? gradient, bool isDark)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        _preview = new PreviewView(wallpaper, gradient, isDark);
        WinoLayout.Size(_preview, TileWidth, PreviewHeight);

        _badge = new WinoSurfaceView { CornerRadius = 9, Fill = WinoStyle.Accent, Hidden = true };
        WinoLayout.Size(_badge, 18, 18);
        var check = new WinoIconView(WinoIconGlyph.Checkmark, 11, UIColor.White) { Colorful = false };
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
        WinoLayout.Fill(stack, this);
        WidthAnchor.ConstraintEqualTo((nfloat)TileWidth).Active = true;

        AccessibilityLabel = name;
        ApplySelection();
    }

    public event EventHandler? Pressed;

    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; ApplySelection(); }
    }

    protected override void OnActivated() => Pressed?.Invoke(this, EventArgs.Empty);

    private void ApplySelection()
    {
        _preview.SelectionStroke = _isSelected ? WinoStyle.Accent : WinoSettingsStyle.CardStroke;
        _preview.SelectionStrokeWidth = _isSelected ? 2 : 1;
        _badge.Fill = WinoStyle.Accent;
        _badge.Hidden = !_isSelected;
        _name.Font = _isSelected ? WinoStyle.Scaled(13, UIFontWeight.Semibold) : WinoSettingsStyle.CardDescription;
        AccessibilityTraits = _isSelected ? UIAccessibilityTrait.Button | UIAccessibilityTrait.Selected : UIAccessibilityTrait.Button;
    }

    /// <summary>Draws the wallpaper aspect-filled (or the gradient) with the window miniature on top.</summary>
    private sealed class PreviewView : UIView
    {
        private const double Radius = 8;
        private readonly UIImage? _wallpaper;
        private readonly uint[]? _gradient;
        private readonly bool _isDark;
        private UIColor _stroke = WinoSettingsStyle.CardStroke;
        private double _strokeWidth = 1;

        public PreviewView(UIImage? wallpaper, uint[]? gradient, bool isDark)
        {
            _wallpaper = wallpaper;
            _gradient = gradient;
            _isDark = isDark;
            TranslatesAutoresizingMaskIntoConstraints = false;
            Opaque = false;
            BackgroundColor = UIColor.Clear;
            ContentMode = UIViewContentMode.Redraw;
            UserInteractionEnabled = false;
            WinoTraits.OnAppearanceChanged(this, static view => view.SetNeedsDisplay());
        }

        public UIColor SelectionStroke { get => _stroke; set { _stroke = value; SetNeedsDisplay(); } }
        public double SelectionStrokeWidth { get => _strokeWidth; set { _strokeWidth = value; SetNeedsDisplay(); } }

        public override void Draw(CGRect rect)
        {
            var bounds = Bounds;
            var context = UIGraphics.GetCurrentContext();
            context.SaveState();
            UIBezierPath.FromRoundedRect(bounds, (nfloat)Radius).AddClip();

            bool systemDark = WinoTraits.IsDark(TraitCollection);
            bool dark = _isDark || (_gradient is null && _wallpaper is null && systemDark);
            if (_wallpaper is { } image && WinoAspectFill.Frame(image.Size.Width, image.Size.Height, bounds.Width, bounds.Height) is { } fill)
            {
                image.Draw(new CGRect(fill.X, fill.Y, fill.Width, fill.Height));
            }
            else if (_gradient is { Length: 4 } stops)
            {
                dark = systemDark;
                var start = WinoStyle.Hex(dark ? stops[2] : stops[0]).CGColor;
                var end = WinoStyle.Hex(dark ? stops[3] : stops[1]).CGColor;
                using var colorSpace = CGColorSpace.CreateSrgb();
                using var gradient = new CGGradient(colorSpace, new[] { start, end });
                context.DrawLinearGradient(gradient, new CGPoint(0, 0), new CGPoint(bounds.Width * 0.32, bounds.Height), CGGradientDrawingOptions.DrawsAfterEndLocation);
            }
            else
            {
                UIColor.SystemBackground.SetFill();
                UIGraphics.RectFill(bounds);
            }

            // Miniature window: sidebar strip and content pane. A plain (Default) background needs tinted shapes;
            // wallpapers take translucent white ones.
            bool plain = _wallpaper is null && _gradient is null;
            var pane = plain
                ? (dark ? WinoStyle.Hex(0xFFFFFF, 0.08) : WinoStyle.Hex(0x000000, 0.05))
                : dark ? WinoStyle.Hex(0x1E1E22, 0.85) : WinoStyle.Hex(0xFFFFFF, 0.88);
            var strip = plain
                ? (dark ? WinoStyle.Hex(0xFFFFFF, 0.12) : WinoStyle.Hex(0x000000, 0.08))
                : dark ? WinoStyle.Hex(0xFFFFFF, 0.18) : WinoStyle.Hex(0xFFFFFF, 0.35);
            var bottom = bounds.Height - 12;
            strip.SetFill();
            UIBezierPath.FromRoundedRect(new CGRect(10, 12, 26, bottom - 10), 3).Fill();
            pane.SetFill();
            UIBezierPath.FromRoundedRect(new CGRect(42, 12, bounds.Width - 52, bottom - 10), 4).Fill();
            context.RestoreState();

            var inset = (nfloat)(_strokeWidth / 2);
            var border = UIBezierPath.FromRoundedRect(bounds.Inset(inset, inset), (nfloat)Radius - inset);
            border.LineWidth = (nfloat)_strokeWidth;
            _stroke.SetStroke();
            border.Stroke();
        }
    }
}
