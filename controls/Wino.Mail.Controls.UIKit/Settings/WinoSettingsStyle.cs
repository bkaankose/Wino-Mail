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
    public const double CardRadius = 10;
    public const double CardPadding = 14;
    public const double CardVerticalPadding = 7;
    public const double IconSize = 22;
    public const double IconGap = 12;
    /// <summary>Nested expander rows and row separators start at the header text column: padding + icon + gap.</summary>
    public const double NestedIndent = CardPadding + IconSize + IconGap;
    /// <summary>Space between cards: every setting is its own card on the page background (lists built from a collection too).</summary>
    public const double CardSpacing = 8;
    /// <summary>Space between groups on a settings page (about 35pt with a section header).</summary>
    public const double GroupSpacing = 18;
    public const double PopUpMinWidth = 120;

    /// <summary>Settings rows (SettingsControls board): 52pt minimum, 8pt vertical padding, 45% disabled alpha.</summary>
    public const double RowMinHeight = 52;
    public const double RowVerticalPadding = 8;
    public const double DisabledAlpha = 0.45;
    /// <summary>Detail page option rows.</summary>
    public const double OptionRowHeight = 48;

    /// <summary>The inset group surface: white in light, the #1E1E23 lift in dark.</summary>
    public static UIColor GroupFill => WinoStyle.Dynamic(UIColor.White, WinoStyle.Hex(0x1E1E23));
    /// <summary>The zone behind settings groups (#F2F2F6 in light), so white groups stand out as on the boards.</summary>
    public static UIColor PageFill => WinoStyle.Dynamic(WinoStyle.Hex(0xF2F2F6), WinoStyle.Hex(0x111114));
    public static UIColor RowSeparator => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.07), WinoStyle.Hex(0xFFFFFF, 0.08));
    public static UIColor Chevron => WinoStyle.Dynamic(WinoStyle.Hex(0xC7C7CC), WinoStyle.Hex(0x5A5A5F));
    public static UIFont RowValue => WinoStyle.Scaled(15);
    /// <summary>Section header over a group: 12pt semibold, upper case.</summary>
    public static UIFont SectionHeader => WinoStyle.Scaled(12, UIFontWeight.Semibold);

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
