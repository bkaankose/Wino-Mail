using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Core.Domain.Presentation;
using Wino.Presentation.UIKit;

namespace Wino.Mail.UIKit.Views.Settings;

/// <summary>
/// UIKit look of the mail filter pages (Mac MailFilterPresentation). Glyphs, tones, names and the rule summary come
/// from the shared <see cref="MailFilterDisplay"/>; this maps the tone to a colour and draws the tile.
/// </summary>
internal static class MailFilterPresentation
{
    /// <summary>Windows GetFilterActionIconForeground: success, caution, critical or attention (accent).</summary>
    public static UIColor ActionTone(MailFilterActionType? action) => MailFilterDisplay.GetActionTone(action) switch
    {
        MailFilterActionTone.Success => WinoStyle.Success,
        MailFilterActionTone.Caution => WinoStyle.Caution,
        MailFilterActionTone.Critical => WinoStyle.Critical,
        _ => WinoStyle.Accent
    };

    /// <summary>A 32pt rounded tile with a tinted wash and a glyph in the tone colour (Windows filter row tile).</summary>
    public static (WinoSurfaceView Tile, WinoIconView Icon) Tile(WinoIconGlyph glyph, UIColor tone, double size = 32, double glyphSize = 15)
    {
        var tile = new WinoSurfaceView { Fill = tone.ColorWithAlpha(0.12f), CornerRadius = 6, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoLayout.Size(tile, size, size);
        var icon = new WinoIconView(glyph, glyphSize, tone) { Colorful = false, TranslatesAutoresizingMaskIntoConstraints = false };
        tile.AddSubview(icon);
        NSLayoutConstraint.ActivateConstraints(
        [
            icon.CenterXAnchor.ConstraintEqualTo(tile.CenterXAnchor),
            icon.CenterYAnchor.ConstraintEqualTo(tile.CenterYAnchor),
            icon.WidthAnchor.ConstraintEqualTo((nfloat)glyphSize),
            icon.HeightAnchor.ConstraintEqualTo((nfloat)glyphSize)
        ]);
        tile.IsAccessibilityElement = false;
        tile.AccessibilityElementsHidden = true;
        return (tile, icon);
    }
}
