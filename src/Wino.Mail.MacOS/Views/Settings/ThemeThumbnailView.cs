using AppKit;
using CoreGraphics;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;
using Wino.Mail.Controls.AppKit.Settings;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// A small, non-interactive picture of a theme: its wallpaper or custom preview (aspect fill), its
/// gradient, or the plain window background for Default. Used on the Personalization gallery card and
/// the gallery's current-theme card.
/// </summary>
internal sealed class ThemeThumbnailView : NSView
{
    private NSImage? _image;
    private uint[]? _gradient;

    public ThemeThumbnailView(double width, double height)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        WinoLayout.Size(this, width, height);
        AccessibilityElement = false;
    }

    /// <summary>Shows <paramref name="theme"/>, or nothing for null.</summary>
    public void Show(AppThemeBase? theme)
    {
        _image = ImageFor(theme);
        _gradient = theme is null || theme.AppThemeType == AppThemeType.Custom ? null : MacWinoThemeService.GradientFor(theme.ThemeName);
        if (_gradient is not null) _image = null;
        NeedsDisplay = true;
    }

    /// <summary>The picture of a theme for tiles and thumbnails: a custom theme's stored preview, or a predefined wallpaper.</summary>
    public static NSImage? ImageFor(AppThemeBase? theme)
    {
        if (theme is null) return null;
        if (theme.AppThemeType == AppThemeType.Custom)
            return string.IsNullOrEmpty(theme.PreviewImage) ? null : MacThemeImaging.DecodeFile(theme.PreviewImage);
        return MacWinoThemeService.GradientFor(theme.ThemeName) is null ? MacWinoThemeService.PreviewImage(theme.ThemeName) : null;
    }

    public override void ViewDidChangeEffectiveAppearance()
    {
        base.ViewDidChangeEffectiveAppearance();
        NeedsDisplay = true;
    }

    public override void DrawRect(CGRect dirtyRect)
    {
        var bounds = Bounds;
        var clip = NSBezierPath.FromRoundedRect(bounds, 5, 5);
        NSGraphicsContext.CurrentContext?.SaveGraphicsState();
        clip.AddClip();
        if (_image is { } image && image.Size.Width > 0 && image.Size.Height > 0)
        {
            image.Draw(MacThemeImaging.Place(image.Size, bounds, ThemeWallpaperFit.Fill, ThemeWallpaperAlignment.Center, IsFlipped),
                CGRect.Empty, NSCompositingOperation.SourceOver, 1);
        }
        else if (_gradient is { Length: 4 } stops)
        {
            bool dark = WinoIcons.IsDark(EffectiveAppearance);
            using var fill = new NSGradient(WinoStyle.Hex(dark ? stops[2] : stops[0]), WinoStyle.Hex(dark ? stops[3] : stops[1]));
            fill.DrawInRect(bounds, -72);
        }
        else
        {
            NSColor.WindowBackground.SetFill();
            NSGraphics.RectFill(bounds);
        }
        NSGraphicsContext.CurrentContext?.RestoreGraphicsState();

        WinoSettingsStyle.CardStroke.SetStroke();
        var border = NSBezierPath.FromRoundedRect(bounds.Inset(0.5f, 0.5f), 4.5f, 4.5f);
        border.LineWidth = 1;
        border.Stroke();
    }
}
