using AppKit;
using CoreGraphics;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Shell;

/// <summary>
/// Paints the selected Wino theme behind the whole window, like the Windows ShellWindow
/// WinoApplicationBackgroundColor: the theme wallpaper (aspect fill) or its light/dark
/// gradient. With the default theme it paints the plain window background, so content
/// always sits on a native surface.
/// </summary>
internal sealed class ThemeBackdropView : NSView
{
    public ThemeBackdropView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        MacWinoThemeService.AppearanceChanged += AppearanceChanged;
    }

    private void AppearanceChanged(object? sender, EventArgs args) => NeedsDisplay = true;

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        NeedsDisplay = true;
    }

    public override void DrawRect(CGRect dirtyRect)
    {
        NSColor.WindowBackground.SetFill();
        NSGraphics.RectFill(Bounds);
        bool dark = EffectiveAppearance.FindBestMatch([NSAppearance.NameAqua, NSAppearance.NameDarkAqua]) == NSAppearance.NameDarkAqua;

        if (MacWinoThemeService.BackdropImage is { } image && image.Size.Width > 0 && image.Size.Height > 0)
        {
            // Aspect fill, anchored to the top like the Windows ImageBrush (UniformToFill).
            var scale = Math.Max(Bounds.Width / image.Size.Width, Bounds.Height / image.Size.Height);
            var size = new CGSize(image.Size.Width * scale, image.Size.Height * scale);
            var target = new CGRect((Bounds.Width - size.Width) / 2, Bounds.Height - size.Height, size.Width, size.Height);
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
