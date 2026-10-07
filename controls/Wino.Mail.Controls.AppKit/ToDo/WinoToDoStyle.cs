using AppKit;
using CoreGraphics;
using Wino.Core.Domain.Enums;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.ToDo;

/// <summary>
/// Tokens and small factories shared by the To Do surfaces (Windows ToDoPage: card fill and
/// stroke, the caution star, the critical overdue hue) and the measured sizes from the design
/// board (task card 54pt, drawer 340pt, header row 30pt).
/// </summary>
public static class WinoToDoStyle
{
    public const double DrawerWidth = 340;
    public const double CompactBreakpoint = 1008;
    public const double TaskRowHeight = 58;
    public const double GroupHeaderHeight = 34;
    public const double CardRadius = 6;
    public const double ChipHeight = 30;

    /// <summary>Windows CardBackgroundFillColorDefault.</summary>
    public static NSColor CardFill => WinoStyle.Dynamic(WinoStyle.Hex(0xF7F7F8), WinoStyle.Hex(0xFFFFFF, 0.06));

    /// <summary>Windows CardStrokeColorDefault.</summary>
    public static NSColor CardStroke => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.07));

    /// <summary>Selected card fill (the list container's selection shows through the card).</summary>
    public static NSColor SelectedFill => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.10));

    /// <summary>Hover fill for cards and drawer rows.</summary>
    public static NSColor HoverFill => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.035), WinoStyle.Hex(0xFFFFFF, 0.045));

    /// <summary>Windows LayerFillColorDefault behind the detail drawer.</summary>
    public static NSColor DrawerFill => WinoStyle.Dynamic(WinoStyle.Hex(0xFFFFFF, 0.5), WinoStyle.Hex(0xFFFFFF, 0.036));

    /// <summary>Windows SystemFillColorCaution: the important star.</summary>
    public static NSColor Important => WinoStyle.Hex(0xE1B12C);

    /// <summary>Windows SystemFillColorCritical: overdue dates and destructive rows.</summary>
    public static NSColor Critical => NSColor.SystemRed;

    public static NSColor Disabled => NSColor.TertiaryLabel;

    /// <summary>Borderless glyph button (Windows TransparentActionButtonStyle), sized like the board's 32×30 header buttons.</summary>
    public static NSButton IconButton(WinoIconGlyph glyph, string label, double glyphSize = 16, double width = 32, double height = 30, NSColor? tint = null)
    {
        var button = new NSButton
        {
            Bordered = false,
            BezelStyle = NSBezelStyle.Inline,
            Title = string.Empty,
            Image = WinoIcons.Image(glyph, glyphSize, tint, label),
            ImagePosition = NSCellImagePosition.ImageOnly,
            ContentTintColor = tint ?? NSColor.Label,
            ToolTip = label,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        WinoAccessibility.Label(button, label);
        WinoLayout.Size(button, width, height);
        return button;
    }

    /// <summary>Swaps the glyph on an <see cref="IconButton"/>; keeps size, tint and label.</summary>
    public static void SetGlyph(NSButton button, WinoIconGlyph glyph, double glyphSize, NSColor? tint = null)
    {
        button.Image = WinoIcons.Image(glyph, glyphSize, tint, button.ToolTip);
        button.ContentTintColor = tint ?? NSColor.Label;
    }

    /// <summary>
    /// A state glyph (star on/off, round check) drawn from the mono font in one colour whatever
    /// the icon style, as Windows draws these with an explicit Foreground.
    /// </summary>
    public static NSImage MonoImage(WinoIconGlyph glyph, double size, NSColor tint, string? accessibilityDescription = null)
    {
        var text = WinoIcons.Glyph(glyph);
        var image = NSImage.ImageWithSize(new CGSize(size, size), false, rect => { WinoIcons.Draw(text, rect, tint, colorful: false); return true; });
        image.Template = false;
        image.AccessibilityDescription = accessibilityDescription;
        return image;
    }

    /// <summary>Sets a mono state glyph on an <see cref="IconButton"/>.</summary>
    public static void SetMonoGlyph(NSButton button, WinoIconGlyph glyph, double glyphSize, NSColor tint)
    {
        button.Image = MonoImage(glyph, glyphSize, tint, button.ToolTip);
        button.ContentTintColor = tint;
    }

    /// <summary>Filled accent push button with white text (Windows AccentButtonStyle).</summary>
    public static NSButton AccentButton(string title, double height = 28)
    {
        var button = new NSButton
        {
            Title = title,
            BezelStyle = NSBezelStyle.Rounded,
            ControlSize = NSControlSize.Regular,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        ApplyAccent(button);
        WinoLayout.Size(button, -1, height);
        return button;
    }

    /// <summary>Re-tints an <see cref="AccentButton"/> after the theme accent changes.</summary>
    public static void ApplyAccent(NSButton button)
    {
        button.BezelColor = WinoStyle.Accent;
        var paragraph = new NSMutableParagraphStyle { Alignment = NSTextAlignment.Center };
        button.AttributedTitle = new Foundation.NSAttributedString(button.Title, new NSStringAttributes
        {
            ForegroundColor = NSColor.White,
            Font = NSFont.SystemFontOfSize(13, NSFontWeight.Semibold),
            ParagraphStyle = paragraph
        });
    }

    /// <summary>A caption label in the secondary colour (Windows CaptionTextBlockStyle).</summary>
    public static NSTextField Caption(string? text = null, NSColor? color = null)
        => WinoStyle.Label(text, WinoStyle.Caption, color ?? WinoStyle.SecondaryText);

    /// <summary>A rounded card surface with the To Do fill and stroke.</summary>
    public static WinoSurfaceView Card(double radius = CardRadius, bool stroked = true)
        => new() { Fill = CardFill, Stroke = stroked ? CardStroke : null, CornerRadius = radius };

    /// <summary>Glyph + caption pair for a metadata line; both hide together.</summary>
    public static (NSStackView Stack, WinoIconView Icon, NSTextField Label) MetaSegment(WinoIconGlyph glyph, NSColor? tint = null)
    {
        var icon = new WinoIconView(glyph, 11, tint ?? WinoStyle.SecondaryText) { Colorful = false };
        var label = Caption(null, tint);
        var stack = WinoLayout.HStack(4, icon, label);
        stack.SetContentCompressionResistancePriority(760, NSLayoutConstraintOrientation.Horizontal);
        return (stack, icon, label);
    }

    public static CGRect Inset(CGRect rect, double dx, double dy) => rect.Inset((nfloat)dx, (nfloat)dy);
}
