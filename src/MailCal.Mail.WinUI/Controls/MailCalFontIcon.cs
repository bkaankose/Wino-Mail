using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Enums;

namespace MailCal.Mail.WinUI.Controls;

/// <summary>
/// Icon from the MailCalIcons fonts. FontFamily comes from the implicit style in Styles/MailCalIcons.xaml,
/// which follows the element theme and the user's icon style, so it is never set here.
/// FontSize is left to FontIcon's default and to the hosting control (AppBarButton, NavigationViewItem...).
/// </summary>
public partial class MailCalFontIcon : FontIcon
{
    [GeneratedDependencyProperty(DefaultValue = MailCalIconGlyph.None)]
    public partial MailCalIconGlyph Icon { get; set; }

    partial void OnIconChanged(MailCalIconGlyph newValue) => Glyph = MailCalIconGlyphs.GetGlyph(newValue);
}
