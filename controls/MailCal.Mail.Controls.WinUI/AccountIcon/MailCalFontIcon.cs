using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace MailCal.Mail.Controls.AccountIcon;

/// <summary>
/// Font icon backed by Mail-Cal's packaged icon font.
/// </summary>
public partial class MailCalFontIcon : FontIcon
{
    internal const string FontFamilyUri = "ms-appx:///MailCal.Mail.Controls.WinUI/Assets/MailCalIcons.ttf#MailCalIcons";

    public MailCalFontIcon()
    {
        FontFamily = new FontFamily(FontFamilyUri);
    }
}

/// <summary>
/// Shareable icon source backed by Mail-Cal's packaged icon font.
/// </summary>
public partial class MailCalFontIconSource : FontIconSource
{
    public MailCalFontIconSource()
    {
        FontFamily = new FontFamily(MailCalFontIcon.FontFamilyUri);
    }

    protected override IconElement CreateIconElementCore() => new MailCalFontIcon();
}
