

#if NET8_0
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
#else
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
#endif
namespace Wino.Controls
{
    public class WinoFontIconSource : Microsoft.UI.Xaml.Controls.FontIconSource
    {
        public WinoIconGlyph Icon
        {
            get { return (WinoIconGlyph)GetValue(IconProperty); }
            set { SetValue(IconProperty, value); }
        }

        public static readonly DependencyProperty IconProperty = DependencyProperty.Register(nameof(Icon), typeof(WinoIconGlyph), typeof(WinoFontIconSource), new PropertyMetadata(WinoIconGlyph.Flag, OnIconChanged));

        public WinoFontIconSource()
        {
            FontFamily = new FontFamily("ms-appx:///Assets/WinoIcons.ttf#WinoIcons");
            FontSize = 32;
        }

        private static void OnIconChanged(DependencyObject obj, DependencyPropertyChangedEventArgs args)
        {
            if (obj is WinoFontIconSource fontIcon)
            {
                fontIcon.UpdateGlyph();
            }
        }

        private void UpdateGlyph()
        {
            Glyph = ControlConstants.WinoIconFontDictionary[Icon];
        }
    }
}
