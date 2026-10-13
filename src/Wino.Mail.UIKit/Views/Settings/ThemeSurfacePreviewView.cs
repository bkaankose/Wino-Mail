using CoreGraphics;
using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// Windows ThemeSurfacePreview: a miniature of the window painted with the palette being edited, over
/// the wallpaper as the app composites it, with the surface of the colour being edited outlined in the
/// accent. The calendar scene shows a strip of hour slots instead of the reader.
/// </summary>
internal sealed class ThemeSurfacePreviewView : UIView
{
    private CustomThemePalette _palette = CustomThemePalette.CreateDefaults(false);
    private UIImage? _wallpaper;
    private UIColor _accent = WinoStyle.Accent;
    private ThemeWallpaperFit _fit = ThemeWallpaperFit.Fill;
    private ThemeWallpaperAlignment _alignment = ThemeWallpaperAlignment.Center;
    private bool _dark;

    public ThemeSurfacePreviewView(CustomThemeColorKey highlightedKey, ThemeSurfaceScene scene, string accessibilityLabel)
    {
        HighlightedKey = highlightedKey;
        Scene = scene;
        TranslatesAutoresizingMaskIntoConstraints = false;
        Opaque = false;
        BackgroundColor = UIColor.Clear;
        ContentMode = UIViewContentMode.Redraw;
        WinoAccessibility.Element(this, UIAccessibilityTrait.Image, accessibilityLabel);
        WinoTraits.OnAppearanceChanged(this, static view => view.SetNeedsDisplay());
    }

    public CustomThemeColorKey HighlightedKey { get; }
    public ThemeSurfaceScene Scene { get; }

    public void Update(CustomThemePalette palette, bool dark, UIImage? wallpaper, ThemeWallpaperFit fit, ThemeWallpaperAlignment alignment, UIColor accent)
    {
        _palette = palette;
        _dark = dark;
        _wallpaper = wallpaper;
        _fit = fit;
        _alignment = alignment;
        _accent = accent;
        SetNeedsDisplay();
    }

    public override void Draw(CGRect rect)
    {
        var bounds = Bounds;
        var context = UIGraphics.GetCurrentContext();
        context.SaveState();
        UIBezierPath.FromRoundedRect(bounds, 6).AddClip();

        (_dark ? WinoStyle.Hex(0x202020) : WinoStyle.Hex(0xF3F3F3)).SetFill();
        UIGraphics.RectFill(bounds);
        if (_wallpaper is { } image && image.Size.Width > 0 && image.Size.Height > 0)
            image.Draw(ThemeImagePlacement.Place(image.Size, bounds, _fit, _alignment));

        // Window: navigation pane on the left, the mail list zone and the reader (or calendar) zone.
        var navigation = new CGRect(6, 6, bounds.Width * 0.22, bounds.Height - 12);
        var listX = navigation.Right + 6;
        var list = new CGRect(listX, 6, bounds.Width * 0.30, bounds.Height - 12);
        var reader = new CGRect(list.Right + 6, 6, bounds.Right - list.Right - 12, bounds.Height - 12);
        var header = new CGRect(list.X + 4, list.Y + 4, list.Width - 8, 12);

        Fill(navigation, _palette.NavigationViewContentBackground, 4);
        Fill(list, _palette.WinoContentZoneBackgroud, 5);
        Fill(header, _palette.MailListHeaderBackgroundColor, 3);
        Ink(list.X + 6, list.Y + 22, list.Width - 12, 4);

        if (Scene == ThemeSurfaceScene.CalendarGrid)
        {
            Fill(reader, _palette.WinoContentZoneBackgroud, 5);
            var slot = (reader.Height - 12) / 6;
            for (int row = 0; row < 6; row++)
            {
                var key = row switch
                {
                    1 or 2 => _palette.CalendarWorkHourBackgroundBrush,
                    3 => _palette.CalendarHoverHourBackgroundBrush,
                    4 => _palette.CalendarSelectedHourBackgroundBrush,
                    _ => _palette.CalendarDefaultHourBackgroundBrush
                };
                Fill(new CGRect(reader.X + 6, reader.Y + 6 + row * slot, reader.Width - 12, slot - 2), key, 2);
            }
        }
        else
        {
            Fill(reader, _palette.ReadingPaneBackgroundColorBrush, 5);
            Ink(reader.X + 8, reader.Y + 10, reader.Width * 0.6, 6);
            for (int line = 0; line < 4; line++) Ink(reader.X + 8, reader.Y + 26 + line * 10, reader.Width - 16 - line * 8, 3);
        }
        context.RestoreState();

        // The surface being edited.
        var highlight = HighlightedKey switch
        {
            CustomThemeColorKey.BaseSurface => bounds.Inset(1.5f, 1.5f),
            CustomThemeColorKey.Navigation => navigation,
            CustomThemeColorKey.Workspace => list,
            CustomThemeColorKey.MailListHeader => header,
            CustomThemeColorKey.ReadingPane => reader,
            _ => reader
        };
        _accent.SetStroke();
        var outline = UIBezierPath.FromRoundedRect(highlight.Inset(-1, -1), 5);
        outline.LineWidth = 2;
        outline.Stroke();
        WinoStyle.Separator.SetStroke();
        var border = UIBezierPath.FromRoundedRect(bounds.Inset(0.5f, 0.5f), 6);
        border.LineWidth = 1;
        border.Stroke();
    }

    private static void Fill(CGRect rect, string? hex, double radius)
    {
        if (WinoStyle.FromHexString(hex) is not { } color) return;
        color.SetFill();
        UIBezierPath.FromRoundedRect(rect, (nfloat)radius).Fill();
    }

    /// <summary>A placeholder text line in ink that reads on the palette's mode.</summary>
    private void Ink(double x, double y, double width, double height)
    {
        (_dark ? WinoStyle.Hex(0xFFFFFF, 0.55) : WinoStyle.Hex(0x000000, 0.45)).SetFill();
        UIBezierPath.FromRoundedRect(new CGRect(x, y, Math.Max(0, width), height), (nfloat)(height / 2)).Fill();
    }
}

/// <summary>Where a wallpaper is drawn in a top-left-origin rect (Mac MacThemeImaging.Place with flipped coordinates).</summary>
internal static class ThemeImagePlacement
{
    /// <summary>Fill covers the bounds anchored at the alignment; Fit shows the whole image centred.</summary>
    public static CGRect Place(CGSize imageSize, CGRect bounds, ThemeWallpaperFit fit, ThemeWallpaperAlignment alignment)
    {
        if (imageSize.Width <= 0 || imageSize.Height <= 0) return bounds;
        var scale = fit == ThemeWallpaperFit.Fit
            ? Math.Min(bounds.Width / imageSize.Width, bounds.Height / imageSize.Height)
            : Math.Max(bounds.Width / imageSize.Width, bounds.Height / imageSize.Height);
        var size = new CGSize(imageSize.Width * scale, imageSize.Height * scale);
        if (fit == ThemeWallpaperFit.Fit) alignment = ThemeWallpaperAlignment.Center;

        double horizontal = alignment switch
        {
            ThemeWallpaperAlignment.TopLeft or ThemeWallpaperAlignment.Left or ThemeWallpaperAlignment.BottomLeft => 0,
            ThemeWallpaperAlignment.TopRight or ThemeWallpaperAlignment.Right or ThemeWallpaperAlignment.BottomRight => 1,
            _ => 0.5
        };
        double vertical = alignment switch
        {
            ThemeWallpaperAlignment.TopLeft or ThemeWallpaperAlignment.Top or ThemeWallpaperAlignment.TopRight => 0,
            ThemeWallpaperAlignment.BottomLeft or ThemeWallpaperAlignment.Bottom or ThemeWallpaperAlignment.BottomRight => 1,
            _ => 0.5
        };
        var x = bounds.X + (bounds.Width - size.Width) * horizontal;
        var y = bounds.Y + (bounds.Height - size.Height) * vertical;
        return new CGRect(x, y, size.Width, size.Height);
    }
}
