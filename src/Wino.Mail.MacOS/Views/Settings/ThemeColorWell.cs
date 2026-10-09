using AppKit;
using CoreGraphics;
using Foundation;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// A colour well for the theme editor. It writes hex values (#RRGGBB, or #AARRGGBB when
/// <see cref="AllowsAlpha"/>) through a trailing 100 ms debounce, so dragging in the colour panel does
/// not queue a theme preview per mouse event; <see cref="Flush"/> writes a pending value at once. The
/// shared colour panel shows its opacity slider only while a well that allows alpha is active.
/// </summary>
internal sealed class ThemeColorWell : NSColorWell
{
    private NSTimer? _pending;
    private string? _pendingHex;

    public ThemeColorWell(bool allowsAlpha, string accessibilityLabel)
    {
        AllowsAlpha = allowsAlpha;
        TranslatesAutoresizingMaskIntoConstraints = false;
        WinoLayout.Size(this, 44, 24);
        AccessibilityLabel = accessibilityLabel;
        Activated += (_, _) => Schedule();
    }

    public bool AllowsAlpha { get; }

    /// <summary>Raised with the debounced hex value the user picked.</summary>
    public event EventHandler<string>? ColorPicked;

    /// <summary>Shows <paramref name="hex"/> without raising <see cref="ColorPicked"/>.</summary>
    public void SetHex(string? hex)
    {
        if (WinoStyle.FromHexString(hex) is { } color) Color = color;
    }

    public override void Activate(bool exclusive)
    {
        // After the base call: exclusive activation deactivates the previous well, which turns the slider off.
        base.Activate(exclusive);
        NSColorPanel.SharedColorPanel.ShowsAlpha = AllowsAlpha;
    }

    public override void Deactivate()
    {
        Flush();
        base.Deactivate();
        NSColorPanel.SharedColorPanel.ShowsAlpha = false;
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

    private void Schedule()
    {
        _pendingHex = ToHex(Color, AllowsAlpha);
        _pending?.Invalidate();
        // Common modes, so the timer also fires while the colour panel tracks the mouse.
        _pending = NSTimer.CreateTimer(0.1, false, _ => Flush());
        NSRunLoop.Current.AddTimer(_pending, NSRunLoopMode.Common);
    }

    public static string ToHex(NSColor color, bool withAlpha)
    {
        var srgb = color.UsingColorSpace(NSColorSpace.SRGBColorSpace) ?? color;
        static int Byte(nfloat component) => (int)Math.Round(Math.Clamp((double)component, 0, 1) * 255);
        var rgb = $"{Byte(srgb.RedComponent):X2}{Byte(srgb.GreenComponent):X2}{Byte(srgb.BlueComponent):X2}";
        return withAlpha ? $"#{Byte(srgb.AlphaComponent):X2}{rgb}" : $"#{rgb}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pending?.Invalidate();
            _pending = null;
            _pendingHex = null;
            ColorPicked = null;
            if (IsActive)
            {
                base.Deactivate();
                NSColorPanel.SharedColorPanel.ShowsAlpha = false;
            }
        }
        base.Dispose(disposing);
    }
}

/// <summary>A colour swatch over a checkerboard, so translucent surface colours read as translucent.</summary>
internal sealed class CheckerSwatchView : NSView
{
    private NSColor? _color;

    public CheckerSwatchView(double size)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WinoLayout.Size(this, size, size);
    }

    public NSColor? Color
    {
        get => _color;
        set { _color = value; NeedsDisplay = true; }
    }

    public override void DrawRect(CGRect dirtyRect)
    {
        var bounds = Bounds;
        NSGraphicsContext.CurrentContext?.SaveGraphicsState();
        NSBezierPath.FromRoundedRect(bounds, 4, 4).AddClip();
        NSColor.White.SetFill();
        NSGraphics.RectFill(bounds);
        WinoStyle.Hex(0xCCCCCC).SetFill();
        const double cell = 5;
        for (double y = 0; y < bounds.Height; y += cell)
            for (double x = ((int)(y / cell) % 2) * cell; x < bounds.Width; x += cell * 2)
                NSGraphics.RectFill(new CGRect(x, y, cell, cell));
        if (_color is { } color)
        {
            color.SetFill();
            NSGraphics.RectFill(bounds);
        }
        NSGraphicsContext.CurrentContext?.RestoreGraphicsState();
        WinoStyle.Separator.SetStroke();
        var border = NSBezierPath.FromRoundedRect(bounds.Inset(0.5f, 0.5f), 4, 4);
        border.LineWidth = 1;
        border.Stroke();
    }
}
