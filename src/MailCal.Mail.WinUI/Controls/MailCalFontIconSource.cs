using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Enums;

namespace MailCal.Mail.WinUI.Controls;

/// <summary>
/// IconSource counterpart of <see cref="MailCalFontIcon"/>. The FontIcon it creates is not a MailCalFontIcon,
/// so the implicit style does not reach it; it always uses the monochrome font.
/// </summary>
public partial class MailCalFontIconSource : FontIconSource
{
    private static readonly FontFamily MonochromeFontFamily = new("ms-appx:///Assets/MailCalIcons.ttf#MailCalIcons");

    [GeneratedDependencyProperty(DefaultValue = MailCalIconGlyph.None)]
    public partial MailCalIconGlyph Icon { get; set; }

    public MailCalFontIconSource()
    {
        FontFamily = MonochromeFontFamily;
    }

    partial void OnIconChanged(MailCalIconGlyph newValue) => Glyph = MailCalIconGlyphs.GetGlyph(newValue);
}
