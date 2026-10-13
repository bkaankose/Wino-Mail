using AppKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Presentation;
using Wino.Presentation.AppKit;

namespace Wino.Mail.MacOS.Views.Settings;

/// <summary>
/// AppKit look of the mail filter pages. Glyphs, tones, names and the rule summary come from the
/// shared <see cref="MailFilterDisplay"/>; this maps the tone to a colour and draws the tile.
/// </summary>
internal static class MailFilterPresentation
{
    /// <summary>Windows GetFilterActionIconForeground: success, caution, critical or attention (accent).</summary>
    public static NSColor ActionTone(MailFilterActionType? action) => MailFilterDisplay.GetActionTone(action) switch
    {
        MailFilterActionTone.Success => WinoStyle.Success,
        MailFilterActionTone.Caution => WinoStyle.Caution,
        MailFilterActionTone.Critical => WinoStyle.Critical,
        _ => WinoStyle.Accent
    };

    /// <summary>A 32pt rounded tile with a tinted wash and a glyph in the tone colour (Windows filter row tile).</summary>
    public static (WinoSurfaceView Tile, WinoIconView Icon) Tile(WinoIconGlyph glyph, NSColor tone, double size = 32, double glyphSize = 15)
    {
        var tile = new WinoSurfaceView { Fill = tone.ColorWithAlphaComponent(0.12f), CornerRadius = 6, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(tile, size, size);
        var icon = new WinoIconView(glyph, glyphSize, tone) { Colorful = false };
        tile.AddSubview(icon);
        NSLayoutConstraint.ActivateConstraints(
        [
            icon.CenterXAnchor.ConstraintEqualTo(tile.CenterXAnchor),
            icon.CenterYAnchor.ConstraintEqualTo(tile.CenterYAnchor),
            icon.WidthAnchor.ConstraintEqualTo((nfloat)glyphSize),
            icon.HeightAnchor.ConstraintEqualTo((nfloat)glyphSize)
        ]);
        tile.AccessibilityElement = false;
        return (tile, icon);
    }
}
