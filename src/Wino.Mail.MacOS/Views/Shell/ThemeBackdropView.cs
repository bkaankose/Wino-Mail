using AppKit;
using CoreGraphics;
using Wino.Core.Domain.Enums;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Shell;

/// <summary>
/// Paints the selected Wino theme behind the whole window, like the Windows ShellWindow
/// WinoApplicationBackgroundColor: the theme wallpaper (aspect fill, or a custom theme's fit and
/// focal point) or its light/dark gradient. With the default theme it paints the plain window background, so content
/// always sits on a native surface. With the translucent window material and no wallpaper or gradient, a
/// behind-window vibrancy view shows the blurred desktop instead.
/// </summary>
internal sealed class ThemeBackdropView : NSView
{
    private readonly NSVisualEffectView _material = new()
    {
        Material = NSVisualEffectMaterial.UnderWindowBackground,
        BlendingMode = NSVisualEffectBlendingMode.BehindWindow,
        State = NSVisualEffectState.FollowsWindowActiveState,
        Hidden = true
    };

    public ThemeBackdropView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WinoLayout.Fill(_material, this);
        MacWinoThemeService.AppearanceChanged += AppearanceChanged;
        UpdateMaterial();
    }

    private void AppearanceChanged(object? sender, EventArgs args)
    {
        UpdateMaterial();
        NeedsDisplay = true;
    }

    private static bool PaintsTheme => MacWinoThemeService.BackdropImage is not null || MacWinoThemeService.BackdropGradient is not null;

    private void UpdateMaterial() => _material.Hidden = !MacWinoThemeService.IsTranslucentWindow || PaintsTheme;

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        NeedsDisplay = true;
    }

    public override void DrawRect(CGRect dirtyRect)
    {
        if (!_material.Hidden) return;
        NSColor.WindowBackground.SetFill();
        NSGraphics.RectFill(Bounds);
        bool dark = EffectiveAppearance.FindBestMatch([NSAppearance.NameAqua, NSAppearance.NameDarkAqua]) == NSAppearance.NameDarkAqua;

        if (MacWinoThemeService.BackdropImage is { } image && image.Size.Width > 0 && image.Size.Height > 0)
        {
            // Predefined wallpapers: aspect fill anchored to the top like the Windows ImageBrush (UniformToFill).
            // Custom wallpapers: the theme's Fill (anchored at its focal point) or Fit (whole image, centred).
            var target = MacThemeImaging.Place(image.Size, Bounds, MacWinoThemeService.BackdropFit,
                MacWinoThemeService.BackdropAlignment ?? ThemeWallpaperAlignment.Top, IsFlipped);
            image.Draw(target, CGRect.Empty, NSCompositingOperation.SourceOver, 1);
            return;
        }

        if (MacWinoThemeService.BackdropGradient is { Length: 4 } stops)
        {
            var start = WinoStyle.Hex(dark ? stops[2] : stops[0]);
            var end = WinoStyle.Hex(dark ? stops[3] : stops[1]);
            using var gradient = new NSGradient(start, end);
            // XAML StartPoint 0,0 to EndPoint 0.32,1 runs top-left to bottom, slightly right: about -72 degrees.
            gradient.DrawInRect(Bounds, -72);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) MacWinoThemeService.AppearanceChanged -= AppearanceChanged;
        base.Dispose(disposing);
    }
}
