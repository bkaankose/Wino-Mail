using UIKit;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Settings;

/// <summary>
/// Settings surface tokens (UIKit twin of the AppKit <c>WinoSettingsStyle</c>): card fill and stroke, the subtle fill
/// under expander rows and the pressed fill, plus the Windows SettingsCard measurements raised to iOS touch sizes.
/// </summary>
public static class WinoSettingsStyle
{
    /// <summary>
    /// Row height, padding and radius: the Windows SettingsCard measurements at the iOS inset-grouped density (44pt rows,
    /// 22pt glyph, 16pt title; rows with a description grow with their text).
    /// </summary>
    public const double CardMinHeight = 44;
    public const double NestedRowMinHeight = 44;
    public const double CardRadius = 20;
    public const double CardPadding = 16;
    public const double CardVerticalPadding = 7;
    public const double IconSize = 22;
    public const double IconGap = 12;
    /// <summary>Nested expander rows and row separators start at the header text column: padding + icon + gap.</summary>
    public const double NestedIndent = CardPadding + IconSize + IconGap;
    /// <summary>Space between cards: every setting is its own card on the page background (lists built from a collection too).</summary>
    public const double CardSpacing = 8;
    /// <summary>Space between groups on a settings page; 20 before a group with a section header (iOS refresh, 14 Nov 2026).</summary>
    public const double GroupSpacing = 28;
    public const double GroupSpacingBeforeHeader = 20;
    public const double PopUpMinWidth = 120;

    /// <summary>Settings rows (iOS refresh, 14 Nov 2026): 44pt for a title, 58pt with a subtitle, 6pt vertical padding.</summary>
    public const double RowMinHeight = 44;
    public const double RowWithSubtitleMinHeight = 58;
    public const double RowVerticalPadding = 6;
    public const double DisabledAlpha = 0.45;
    /// <summary>Detail page option rows.</summary>
    public const double OptionRowHeight = 48;

    /// <summary>The inset group surface: white in light, the #1E1E23 lift in dark.</summary>
    public static UIColor GroupFill => WinoStyle.Dynamic(UIColor.White, WinoStyle.Hex(0x1E1E23));
    /// <summary>The page behind settings groups, root and sub pages alike.</summary>
    public static UIColor PageFill => WinoStyle.Dynamic(WinoStyle.Hex(0xF2F2F7), WinoStyle.Hex(0x0E0E11));
    /// <summary>The group outline: none in light, a 7% white line in dark.</summary>
    public static UIColor GroupStroke => WinoStyle.Dynamic(UIColor.Clear, WinoStyle.Hex(0xFFFFFF, 0.07));
    public static UIColor RowSeparator => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.07), WinoStyle.Hex(0xFFFFFF, 0.08));
    public static UIColor Chevron => WinoStyle.Dynamic(WinoStyle.Hex(0xC7C7CC), WinoStyle.Hex(0x5A5A5F));
    public static UIFont RowValue => WinoStyle.Scaled(16);
    /// <summary>Section header over a group: 13pt regular, sentence case.</summary>
    public static UIFont SectionHeader => WinoStyle.Scaled(13);

    public static UIColor CardFill => WinoStyle.Dynamic(WinoStyle.Hex(0xF7F7F8), WinoStyle.Hex(0xFFFFFF, 0.06));
    public static UIColor CardStroke => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.07));
    public static UIColor SubtleFill => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.05), WinoStyle.Hex(0xFFFFFF, 0.08));
    public static UIColor SelectedFill => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.10));
    public static UIColor PaneVeil => WinoStyle.Dynamic(WinoStyle.Hex(0xFFFFFF, 0.45), WinoStyle.Hex(0x14141A, 0.45));

    public static UIFont SectionTitle => WinoStyle.Scaled(15, UIFontWeight.Semibold);
    /// <summary>The inset-grouped section header over a group of rows (13pt, secondary text).</summary>
    public static UIFont GroupHeader => WinoStyle.Scaled(13);
    public static UIFont PageTitle => WinoStyle.Scaled(28, UIFontWeight.Bold);
    public static UIFont CardTitle => WinoStyle.Scaled(16);
    public static UIFont CardDescription => WinoStyle.Scaled(13);
}
