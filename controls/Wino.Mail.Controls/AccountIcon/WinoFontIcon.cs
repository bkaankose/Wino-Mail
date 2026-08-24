using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Wino.Mail.Controls.AccountIcon;

/// <summary>
/// Font icon backed by Wino's packaged icon font.
/// </summary>
public partial class WinoFontIcon : FontIcon
{
    internal const string FontFamilyUri = "ms-appx:///Wino.Mail.Controls/Assets/WinoIcons.ttf#WinoIcons";

    public WinoFontIcon()
    {
        FontFamily = new FontFamily(FontFamilyUri);
    }
}

/// <summary>
/// Shareable icon source backed by Wino's packaged icon font.
/// </summary>
public partial class WinoFontIconSource : FontIconSource
{
    public WinoFontIconSource()
    {
        FontFamily = new FontFamily(WinoFontIcon.FontFamilyUri);
    }

    protected override IconElement CreateIconElementCore() => new WinoFontIcon();
}
