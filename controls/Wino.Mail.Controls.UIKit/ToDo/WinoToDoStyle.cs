using UIKit;
using Wino.Core.Domain.Enums;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.ToDo;

/// <summary>
/// Tokens and small factories shared by the To Do surfaces (UIKit twin of the AppKit <c>WinoToDoStyle</c>; Windows
/// ToDoPage card fill and stroke, the caution star, the critical overdue hue) with iOS touch sizes.
/// </summary>
public static class WinoToDoStyle
{
    public const double TaskRowHeight = 64;
    public const double GroupHeaderHeight = 38;
    public const double CardRadius = 8;
    public const double ChipHeight = 34;

    public static UIColor CardFill => WinoStyle.Dynamic(WinoStyle.Hex(0xF7F7F8), WinoStyle.Hex(0xFFFFFF, 0.06));
    public static UIColor CardStroke => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.07));
    public static UIColor SelectedFill => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.10));
    public static UIColor HoverFill => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.035), WinoStyle.Hex(0xFFFFFF, 0.045));

    /// <summary>Windows SystemFillColorCaution: the important star.</summary>
    public static UIColor Important => WinoStyle.Hex(0xE1B12C);

    /// <summary>Windows SystemFillColorCritical: overdue dates and destructive rows.</summary>
    public static UIColor Critical => UIColor.SystemRed;

    public static UIColor Disabled => UIColor.TertiaryLabel;

    /// <summary>Borderless glyph button (Windows TransparentActionButtonStyle) with a 44pt touch target.</summary>
    public static UIButton IconButton(WinoIconGlyph glyph, string label, double glyphSize = 16, double width = 44, double height = 44, UIColor? tint = null)
    {
        var configuration = UIButtonConfiguration.PlainButtonConfiguration;
        configuration.Image = WinoIcons.Image(glyph, glyphSize, tint, label);
        configuration.BaseForegroundColor = tint ?? UIColor.Label;
        configuration.ContentInsets = new NSDirectionalEdgeInsets(0, 0, 0, 0);
        var button = new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
        WinoAccessibility.Label(button, label);
        WinoLayout.Size(button, width, height);
        return button;
    }

    /// <summary>Swaps the glyph on an <see cref="IconButton"/>; keeps size and label.</summary>
    public static void SetGlyph(UIButton button, WinoIconGlyph glyph, double glyphSize, UIColor? tint = null)
    {
        if (button.Configuration is not { } configuration) return;
        configuration.Image = WinoIcons.Image(glyph, glyphSize, tint, button.AccessibilityLabel);
        configuration.BaseForegroundColor = tint ?? UIColor.Label;
        button.Configuration = configuration;
    }

    /// <summary>A state glyph (star on/off, round check) drawn from the mono font in one colour whatever the icon style.</summary>
    public static void SetMonoGlyph(UIButton button, WinoIconGlyph glyph, double glyphSize, UIColor tint)
    {
        if (button.Configuration is not { } configuration) return;
        configuration.Image = WinoIcons.MonoImage(glyph, glyphSize, tint, button.AccessibilityLabel);
        configuration.BaseForegroundColor = tint;
        button.Configuration = configuration;
    }

    /// <summary>Filled accent button with white text (Windows AccentButtonStyle).</summary>
    public static UIButton AccentButton(string title)
    {
        var configuration = UIButtonConfiguration.FilledButtonConfiguration;
        configuration.Title = title;
        configuration.BaseBackgroundColor = WinoStyle.Accent;
        configuration.BaseForegroundColor = UIColor.White;
        configuration.ButtonSize = UIButtonConfigurationSize.Small;
        return new UIButton { Configuration = configuration, TranslatesAutoresizingMaskIntoConstraints = false };
    }

    /// <summary>Re-tints an <see cref="AccentButton"/> after the theme accent changes.</summary>
    public static void ApplyAccent(UIButton button)
    {
        if (button.Configuration is not { } configuration) return;
        configuration.BaseBackgroundColor = WinoStyle.Accent;
        button.Configuration = configuration;
    }

    /// <summary>A caption label in the secondary colour (Windows CaptionTextBlockStyle).</summary>
    public static UILabel Caption(string? text = null, UIColor? color = null)
        => WinoStyle.Label(text, WinoStyle.Caption, color ?? WinoStyle.SecondaryText);

    /// <summary>A rounded card surface with the To Do fill and stroke.</summary>
    public static WinoSurfaceView Card(double radius = CardRadius, bool stroked = true)
        => new() { Fill = CardFill, Stroke = stroked ? CardStroke : null, CornerRadius = radius };

    /// <summary>Glyph + caption pair for a metadata line; both hide together.</summary>
    public static (UIStackView Stack, WinoIconView Icon, UILabel Label) MetaSegment(WinoIconGlyph glyph, UIColor? tint = null)
    {
        var icon = new WinoIconView(glyph, 11, tint ?? WinoStyle.SecondaryText) { Colorful = false };
        var label = Caption(null, tint);
        var stack = WinoLayout.HStack(4, icon, label);
        stack.SetContentCompressionResistancePriority(760, UILayoutConstraintAxis.Horizontal);
        return (stack, icon, label);
    }
}
