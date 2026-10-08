using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;
using Wino.Core.Domain;
using Wino.Core.Domain.Enums;

namespace Wino.Mail.WinUI.Controls;

/// <summary>
/// Icon from the WinoIcons fonts. FontFamily comes from the implicit style in Styles/WinoIcons.xaml,
/// which follows the element theme and the user's icon style, so it is never set here.
/// FontSize is left to FontIcon's default and to the hosting control (AppBarButton, NavigationViewItem...).
/// </summary>
public partial class WinoFontIcon : FontIcon
{
    [GeneratedDependencyProperty(DefaultValue = WinoIconGlyph.None)]
    public partial WinoIconGlyph Icon { get; set; }

    partial void OnIconChanged(WinoIconGlyph newValue) => Glyph = WinoIconGlyphs.GetGlyph(newValue);
}
