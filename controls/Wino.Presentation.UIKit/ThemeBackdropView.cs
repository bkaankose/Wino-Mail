using CoreAnimation;
using CoreGraphics;
using UIKit;
using Wino.Mail.Controls.Core.Layout;

namespace Wino.Presentation.UIKit;

/// <summary>
/// Paints the selected Wino theme behind the whole screen (the Windows WinoApplicationBackgroundColor and the Mac
/// ThemeBackdropView): the theme wallpaper, aspect-filled and anchored to the top like the Windows ImageBrush, or its
/// light and dark gradient. Without either it shows the plain system background, so content always sits on a native
/// surface. The theme service owns the inputs and calls <see cref="SetBackdrop"/>.
/// </summary>
public sealed class ThemeBackdropView : UIView
{
    private readonly CAGradientLayer _gradient = new()
    {
        // XAML StartPoint 0,0 to EndPoint 0.32,1: top-left to bottom, slightly right.
        StartPoint = new CGPoint(0, 0),
        EndPoint = new CGPoint(0.32, 1),
        Hidden = true
    };

    private UIImage? _image;
    private uint[]? _stops;

    public ThemeBackdropView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        ContentMode = UIViewContentMode.Redraw;
        BackgroundColor = UIColor.SystemBackground;
        UserInteractionEnabled = false;
        Layer.AddSublayer(_gradient);
        WinoTraits.OnAppearanceChanged(this, static view => view.Refresh());
    }

    /// <summary>The wallpaper, when the theme has one.</summary>
    public UIImage? Image => _image;

    /// <summary>
    /// Shows <paramref name="image"/> (aspect fill, top anchored) or the four-stop <paramref name="gradient"/>
    /// (light start, light end, dark start, dark end as 0xRRGGBB); both null shows the system background.
    /// </summary>
    public void SetBackdrop(UIImage? image, uint[]? gradient)
    {
        _image = image;
        _stops = gradient is { Length: 4 } ? gradient : null;
        Refresh();
    }

    /// <summary>True when a wallpaper or gradient is shown (zones then float over it).</summary>
    public bool PaintsTheme => _image is not null || _stops is not null;

    private void Refresh()
    {
        if (_stops is { } stops && _image is null)
        {
            bool dark = WinoTraits.IsDark(TraitCollection);
            _gradient.Colors = [WinoStyle.Hex(dark ? stops[2] : stops[0]).CGColor, WinoStyle.Hex(dark ? stops[3] : stops[1]).CGColor];
            _gradient.Hidden = false;
        }
        else
        {
            _gradient.Hidden = true;
        }
        SetNeedsDisplay();
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        CATransaction.Begin();
        CATransaction.DisableActions = true;
        _gradient.Frame = Bounds;
        CATransaction.Commit();
    }

    public override void Draw(CGRect rect)
    {
        if (_image is not { } image) return;
        if (WinoAspectFill.Frame(image.Size.Width, image.Size.Height, Bounds.Width, Bounds.Height) is not { } fill) return;
        // Anchored to the top: the overflow is cut at the bottom, as with UniformToFill and AlignmentY=Top.
        image.Draw(new CGRect(fill.X, 0, fill.Width, fill.Height));
    }
}

/// <summary>
/// The material behind bars and the sidebar. With a Wino theme the wallpaper shows through a system chrome blur softened
/// by a light or dark veil; with the default theme it is the plain bar material.
/// </summary>
public sealed class WinoBarBackdropView : UIView
{
    private readonly UIVisualEffectView _effect = new(UIBlurEffect.FromStyle(UIBlurEffectStyle.SystemChromeMaterial))
    {
        TranslatesAutoresizingMaskIntoConstraints = false
    };

    private readonly WinoSurfaceView _veil = new()
    {
        Fill = WinoStyle.Dynamic(WinoStyle.Hex(0xFFFFFF, 0.22), WinoStyle.Hex(0x14141A, 0.28))
    };

    public WinoBarBackdropView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        UserInteractionEnabled = false;
        WinoLayout.Fill(_effect, this);
        WinoLayout.Fill(_veil, this);
        Apply();
        WinoStyle.BackdropChanged += BackdropChanged;
    }

    private void BackdropChanged(object? sender, EventArgs args) => Apply();

    private void Apply() => _veil.Hidden = !WinoStyle.HasBackdrop;

    protected override void Dispose(bool disposing)
    {
        if (disposing) WinoStyle.BackdropChanged -= BackdropChanged;
        base.Dispose(disposing);
    }
}
