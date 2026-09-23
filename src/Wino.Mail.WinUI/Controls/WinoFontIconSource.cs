using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Controls;

/// <summary>
/// IconSource counterpart of <see cref="WinoFontIcon"/>. The FontIcon it creates is not a WinoFontIcon,
/// so the implicit style does not reach it; it always uses the monochrome font.
/// </summary>
public partial class WinoFontIconSource : FontIconSource
{
    private static readonly FontFamily MonochromeFontFamily = new("ms-appx:///Assets/WinoIcons.ttf#WinoIcons");

    [GeneratedDependencyProperty(DefaultValue = WinoIconGlyph.None)]
    public partial WinoIconGlyph Icon { get; set; }

    public WinoFontIconSource()
    {
        FontFamily = MonochromeFontFamily;
    }

    partial void OnIconChanged(WinoIconGlyph newValue) => Glyph = WinoIconGlyphs.GetGlyph(newValue);
}
