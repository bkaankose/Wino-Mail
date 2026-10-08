using AppKit;
using Wino.Presentation.AppKit;

namespace Wino.Mail.Controls.AppKit.Settings;

/// <summary>
/// Settings surface tokens from the Wino parity boards (design/lib.py Theme): card fill and stroke,
/// the subtle fill under expander rows and the sidebar selection fill. All resolve per appearance.
/// </summary>
public static class WinoSettingsStyle
{
    /// <summary>Card height, padding and radius of the Windows SettingsCard.</summary>
    public const double CardMinHeight = 62;
    public const double NestedRowMinHeight = 48;
    public const double CardRadius = 6;
    public const double CardPadding = 16;
    public const double CardVerticalPadding = 10;
    public const double IconSize = 20;
    public const double IconGap = 16;
    /// <summary>Nested expander rows start at the header text column: padding + icon + gap.</summary>
    public const double NestedIndent = CardPadding + IconSize + IconGap;
    public const double CardSpacing = 4;
    public const double PopUpMinWidth = 180;

    public static NSColor CardFill => WinoStyle.Dynamic(WinoStyle.Hex(0xF7F7F8), WinoStyle.Hex(0xFFFFFF, 0.06));
    public static NSColor CardStroke => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.07));
    public static NSColor SubtleFill => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.05), WinoStyle.Hex(0xFFFFFF, 0.08));
    public static NSColor SelectedFill => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.10));
    public static NSColor PaneVeil => WinoStyle.Dynamic(WinoStyle.Hex(0xFFFFFF, 0.45), WinoStyle.Hex(0x14141A, 0.45));

    public static NSFont SectionTitle => NSFont.SystemFontOfSize(13, NSFontWeight.Semibold);
    public static NSFont PageTitle => NSFont.SystemFontOfSize(24, NSFontWeight.Semibold);
    public static NSFont CardTitle => NSFont.SystemFontOfSize(13);
    public static NSFont CardDescription => NSFont.SystemFontOfSize(12);
}
