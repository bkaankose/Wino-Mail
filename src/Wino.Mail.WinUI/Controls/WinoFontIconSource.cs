using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;
using WinoFontIconFamily = Wino.Mail.Controls.AccountIcon.WinoFontIconFamily;

namespace Wino.Mail.WinUI.Controls;

/// <summary>
/// IconSource counterpart of <see cref="WinoFontIcon"/>. The FontIcon it creates is not a WinoFontIcon,
/// so the implicit style does not reach it; it always uses the monochrome font of the user's icon set
/// (<see cref="WinoFontIconFamily"/>, kept current by NewThemeService).
/// </summary>
public partial class WinoFontIconSource : FontIconSource
{
    [GeneratedDependencyProperty(DefaultValue = WinoIconGlyph.None)]
    public partial WinoIconGlyph Icon { get; set; }

    public WinoFontIconSource()
    {
        FontFamily = WinoFontIconFamily.Current;
        WinoFontIconFamily.Track(this);
    }

    partial void OnIconChanged(WinoIconGlyph newValue) => Glyph = WinoIconGlyphs.GetGlyph(newValue);
}
