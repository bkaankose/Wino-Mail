using AppKit;
using CoreGraphics;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Models.Personalization;
using Wino.Mail.MacOS.Infrastructure;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// Windows ThemeSurfacePreview: a miniature of the window painted with the palette being edited, over
/// the wallpaper as the app composites it, with the surface of the colour being edited outlined in the
/// accent. The calendar scene shows a strip of hour slots instead of the reader.
/// </summary>
internal sealed class ThemeSurfacePreviewView : NSView
{
    private CustomThemePalette _palette = CustomThemePalette.CreateDefaults(false);
    private NSImage? _wallpaper;
    private NSColor _accent = NSColor.ControlAccent;
    private ThemeWallpaperFit _fit = ThemeWallpaperFit.Fill;
    private ThemeWallpaperAlignment _alignment = ThemeWallpaperAlignment.Center;
    private bool _dark;

    public ThemeSurfacePreviewView(CustomThemeColorKey highlightedKey, ThemeSurfaceScene scene, string accessibilityLabel)
    {
        HighlightedKey = highlightedKey;
        Scene = scene;
        TranslatesAutoresizingMaskIntoConstraints = false;
        AccessibilityElement = true;
        AccessibilityRole = NSAccessibilityRoles.ImageRole;
        AccessibilityLabel = accessibilityLabel;
    }

    public CustomThemeColorKey HighlightedKey { get; }
    public ThemeSurfaceScene Scene { get; }

    public override bool IsFlipped => true;

    public void Update(CustomThemePalette palette, bool dark, NSImage? wallpaper, ThemeWallpaperFit fit, ThemeWallpaperAlignment alignment, NSColor accent)
    {
        _palette = palette;
        _dark = dark;
        _wallpaper = wallpaper;
        _fit = fit;
        _alignment = alignment;
        _accent = accent;
        NeedsDisplay = true;
    }

    public override void DrawRect(CGRect dirtyRect)
    {
        var bounds = Bounds;
        NSGraphicsContext.CurrentContext?.SaveGraphicsState();
        NSBezierPath.FromRoundedRect(bounds, 6, 6).AddClip();

        (_dark ? WinoStyle.Hex(0x202020) : WinoStyle.Hex(0xF3F3F3)).SetFill();
        NSGraphics.RectFill(bounds);
        if (_wallpaper is { } image && image.Size.Width > 0 && image.Size.Height > 0)
            image.Draw(MacThemeImaging.Place(image.Size, bounds, _fit, _alignment, flipped: true), CGRect.Empty, NSCompositingOperation.SourceOver, 1, true, null);

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
        NSGraphicsContext.CurrentContext?.RestoreGraphicsState();

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
        var outline = NSBezierPath.FromRoundedRect(highlight.Inset(-1, -1), 5, 5);
        outline.LineWidth = 2;
        outline.Stroke();
        WinoStyle.Separator.SetStroke();
        var border = NSBezierPath.FromRoundedRect(bounds.Inset(0.5f, 0.5f), 6, 6);
        border.LineWidth = 1;
        border.Stroke();
    }

    private static void Fill(CGRect rect, string? hex, double radius)
    {
        if (WinoStyle.FromHexString(hex) is not { } color) return;
        color.SetFill();
        NSBezierPath.FromRoundedRect(rect, (nfloat)radius, (nfloat)radius).Fill();
    }

    /// <summary>A placeholder text line in ink that reads on the palette's mode.</summary>
    private void Ink(double x, double y, double width, double height)
    {
        (_dark ? WinoStyle.Hex(0xFFFFFF, 0.55) : WinoStyle.Hex(0x000000, 0.45)).SetFill();
        NSBezierPath.FromRoundedRect(new CGRect(x, y, Math.Max(0, width), height), (nfloat)(height / 2), (nfloat)(height / 2)).Fill();
    }
}
