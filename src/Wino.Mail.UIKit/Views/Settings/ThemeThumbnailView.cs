using CoreGraphics;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;
using Wino.Mail.Controls.UIKit.Settings;
using Wino.Mail.UIKit.Infrastructure;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// A small, non-interactive picture of a theme: its wallpaper or custom preview (aspect fill), its
/// gradient, or the plain window background for Default. Used on the Personalization gallery card and
/// the gallery's current-theme card.
/// </summary>
internal sealed class ThemeThumbnailView : UIView
{
    private UIImage? _image;
    private uint[]? _gradient;

    public ThemeThumbnailView(double width, double height)
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        Opaque = false;
        BackgroundColor = UIColor.Clear;
        ContentMode = UIViewContentMode.Redraw;
        UserInteractionEnabled = false;
        IsAccessibilityElement = false;
        WinoLayout.Size(this, width, height);
        WinoTraits.OnAppearanceChanged(this, static view => view.SetNeedsDisplay());
    }

    /// <summary>Shows <paramref name="theme"/>, or nothing for null.</summary>
    public void Show(AppThemeBase? theme)
    {
        _image = ImageFor(theme);
        _gradient = theme is null || theme.AppThemeType == AppThemeType.Custom ? null : UIKitThemeService.GradientFor(theme.ThemeName);
        if (_gradient is not null) _image = null;
        SetNeedsDisplay();
    }

    /// <summary>The picture of a theme for tiles and thumbnails: a custom theme's stored preview, or a predefined wallpaper.</summary>
    public static UIImage? ImageFor(AppThemeBase? theme)
    {
        if (theme is null) return null;
        if (theme.AppThemeType == AppThemeType.Custom)
            return string.IsNullOrEmpty(theme.PreviewImage) || !File.Exists(theme.PreviewImage) ? null : UIImage.FromFile(theme.PreviewImage);
        return UIKitThemeService.GradientFor(theme.ThemeName) is null && theme.ThemeName is { } name ? UIKitThemeService.PreviewImage(name) : null;
    }

    public override void Draw(CGRect rect)
    {
        var bounds = Bounds;
        var context = UIGraphics.GetCurrentContext();
        context.SaveState();
        UIBezierPath.FromRoundedRect(bounds, 5).AddClip();
        if (_image is { } image && image.Size.Width > 0 && image.Size.Height > 0)
        {
            image.Draw(ThemeImagePlacement.Place(image.Size, bounds, ThemeWallpaperFit.Fill, ThemeWallpaperAlignment.Center));
        }
        else if (_gradient is { Length: 4 } stops)
        {
            bool dark = WinoTraits.IsDark(TraitCollection);
            using var colorSpace = CGColorSpace.CreateSrgb();
            using var gradient = new CGGradient(colorSpace, new[] { WinoStyle.Hex(dark ? stops[2] : stops[0]).CGColor, WinoStyle.Hex(dark ? stops[3] : stops[1]).CGColor });
            context.DrawLinearGradient(gradient, new CGPoint(0, 0), new CGPoint(bounds.Width * 0.32, bounds.Height), CGGradientDrawingOptions.DrawsAfterEndLocation);
        }
        else
        {
            UIColor.SystemBackground.SetFill();
            UIGraphics.RectFill(bounds);
        }
        context.RestoreState();

        WinoSettingsStyle.CardStroke.SetStroke();
        var border = UIBezierPath.FromRoundedRect(bounds.Inset(0.5f, 0.5f), 4.5f);
        border.LineWidth = 1;
        border.Stroke();
    }
}
