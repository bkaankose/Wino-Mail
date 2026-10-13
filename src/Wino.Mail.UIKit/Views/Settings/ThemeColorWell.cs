using CoreGraphics;
using Foundation;
using UIKit;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// A colour well for the theme editor (Mac ThemeColorWell): the system <see cref="UIColorWell"/>, which presents
/// <see cref="UIColorPickerViewController"/>, writing hex values (#RRGGBB, or #AARRGGBB when <see cref="AllowsAlpha"/>)
/// through a trailing 100 ms debounce, so dragging in the picker does not queue a theme preview per change;
/// <see cref="Flush"/> writes a pending value at once. 44×44 touch target.
/// </summary>
internal sealed class ThemeColorWell : UIColorWell
{
    private NSTimer? _pending;
    private string? _pendingHex;
    private bool _settingColor;

    public ThemeColorWell(bool allowsAlpha, string accessibilityLabel)
    {
        AllowsAlpha = allowsAlpha;
        SupportsAlpha = allowsAlpha;
        Title = accessibilityLabel;
        TranslatesAutoresizingMaskIntoConstraints = false;
        WinoLayout.Size(this, 44, 44);
        WinoAccessibility.Label(this, accessibilityLabel);
        ValueChanged += Changed;
    }

    public bool AllowsAlpha { get; }

    /// <summary>Raised with the debounced hex value the user picked.</summary>
    public event EventHandler<string>? ColorPicked;

    /// <summary>Shows <paramref name="hex"/> without raising <see cref="ColorPicked"/>.</summary>
    public void SetHex(string? hex)
    {
        if (WinoStyle.FromHexString(hex) is not { } color) return;
        _settingColor = true;
        try { SelectedColor = color; }
        finally { _settingColor = false; }
    }

    /// <summary>Writes a pending colour now.</summary>
    public void Flush()
    {
        _pending?.Invalidate();
        _pending = null;
        if (_pendingHex is not { } hex) return;
        _pendingHex = null;
        ColorPicked?.Invoke(this, hex);
    }

    private void Changed(object? sender, EventArgs args)
    {
        if (_settingColor || SelectedColor is not { } color) return;
        _pendingHex = ToHex(color, AllowsAlpha);
        _pending?.Invalidate();
        _pending = NSTimer.CreateTimer(0.1, false, _ => Flush());
        // Common modes, so the timer also fires while the picker tracks a drag.
        NSRunLoop.Current.AddTimer(_pending, NSRunLoopMode.Common);
    }

    public static string ToHex(UIColor color, bool withAlpha)
    {
        color.GetRGBA(out var red, out var green, out var blue, out var alpha);
        static int Byte(nfloat component) => (int)Math.Round(Math.Clamp((double)component, 0, 1) * 255);
        var rgb = $"{Byte(red):X2}{Byte(green):X2}{Byte(blue):X2}";
        return withAlpha ? $"#{Byte(alpha):X2}{rgb}" : $"#{rgb}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ValueChanged -= Changed;
            _pending?.Invalidate();
            _pending = null;
            _pendingHex = null;
            ColorPicked = null;
        }
        base.Dispose(disposing);
    }
}

/// <summary>A colour swatch over a checkerboard, so translucent surface colours read as translucent.</summary>
internal sealed class CheckerSwatchView : UIView
{
    private UIColor? _color;

    public CheckerSwatchView(double size)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Opaque = false;
        BackgroundColor = UIColor.Clear;
        ContentMode = UIViewContentMode.Redraw;
        UserInteractionEnabled = false;
        WinoLayout.Size(this, size, size);
        WinoTraits.OnAppearanceChanged(this, static view => view.SetNeedsDisplay());
    }

    public UIColor? Color
    {
        get => _color;
        set { _color = value; SetNeedsDisplay(); }
    }

    public override void Draw(CGRect rect)
    {
        var bounds = Bounds;
        var context = UIGraphics.GetCurrentContext();
        context.SaveState();
        UIBezierPath.FromRoundedRect(bounds, 4).AddClip();
        UIColor.White.SetFill();
        UIGraphics.RectFill(bounds);
        WinoStyle.Hex(0xCCCCCC).SetFill();
        const double cell = 5;
        for (double y = 0; y < bounds.Height; y += cell)
            for (double x = ((int)(y / cell) % 2) * cell; x < bounds.Width; x += cell * 2)
                UIGraphics.RectFill(new CGRect(x, y, cell, cell));
        if (_color is { } color)
        {
            color.SetFill();
            UIGraphics.RectFill(bounds);
        }
        context.RestoreState();
        WinoStyle.Separator.SetStroke();
        var border = UIBezierPath.FromRoundedRect(bounds.Inset(0.5f, 0.5f), 4);
        border.LineWidth = 1;
        border.Stroke();
    }
}
