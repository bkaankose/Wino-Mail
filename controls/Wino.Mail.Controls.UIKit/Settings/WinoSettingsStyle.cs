using UIKit;
using Wino.Presentation.UIKit;

namespace Wino.Mail.Controls.UIKit.Settings;

/// <summary>
/// Settings surface tokens (UIKit twin of the AppKit <c>WinoSettingsStyle</c>): card fill and stroke, the subtle fill
/// under expander rows and the pressed fill, plus the Windows SettingsCard measurements raised to iOS touch sizes.
/// </summary>
public static class WinoSettingsStyle
{
    /// <summary>Card height, padding and radius of the Windows SettingsCard.</summary>
    public const double CardMinHeight = 62;
    public const double NestedRowMinHeight = 50;
    public const double CardRadius = 8;
    public const double CardPadding = 16;
    public const double CardVerticalPadding = 10;
    public const double IconSize = 20;
    public const double IconGap = 16;
    /// <summary>Nested expander rows start at the header text column: padding + icon + gap.</summary>
    public const double NestedIndent = CardPadding + IconSize + IconGap;
    public const double CardSpacing = 4;
    public const double PopUpMinWidth = 120;

    public static UIColor CardFill => WinoStyle.Dynamic(WinoStyle.Hex(0xF7F7F8), WinoStyle.Hex(0xFFFFFF, 0.06));
    public static UIColor CardStroke => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.07));
    public static UIColor SubtleFill => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.05), WinoStyle.Hex(0xFFFFFF, 0.08));
    public static UIColor SelectedFill => WinoStyle.Dynamic(WinoStyle.Hex(0x000000, 0.06), WinoStyle.Hex(0xFFFFFF, 0.10));
    public static UIColor PaneVeil => WinoStyle.Dynamic(WinoStyle.Hex(0xFFFFFF, 0.45), WinoStyle.Hex(0x14141A, 0.45));

    public static UIFont SectionTitle => WinoStyle.Scaled(15, UIFontWeight.Semibold);
    public static UIFont PageTitle => WinoStyle.Scaled(28, UIFontWeight.Bold);
    public static UIFont CardTitle => WinoStyle.Scaled(16);
    public static UIFont CardDescription => WinoStyle.Scaled(13);
}
