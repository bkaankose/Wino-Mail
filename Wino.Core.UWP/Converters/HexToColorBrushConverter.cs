using System;
using System.Globalization;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;

namespace Wino.Core.UWP.Converters;

public partial class HexToColorBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string hexColor && !string.IsNullOrEmpty(hexColor))
        {
            try
            {
                // Remove # if present
                hexColor = hexColor.Replace("#", "");

                // Parse hex to color
                byte r = byte.Parse(hexColor.Substring(0, 2), System.Globalization.NumberStyles.HexNumber);
                byte g = byte.Parse(hexColor.Substring(2, 2), System.Globalization.NumberStyles.HexNumber);
                byte b = byte.Parse(hexColor.Substring(4, 2), System.Globalization.NumberStyles.HexNumber);

                return new SolidColorBrush(Color.FromArgb(255, r, g, b));
            }
            catch
            {
                return GetDefaultBrush();
            }
        }

        return GetDefaultBrush();
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is string hexColor)
        {
            try
            {
                // Remove # if present
                hexColor = hexColor.Replace("#", string.Empty);

                // Parse ARGB values
                byte a = 255; // Default to fully opaque
                byte r = byte.Parse(hexColor.Substring(0, 2), NumberStyles.HexNumber);
                byte g = byte.Parse(hexColor.Substring(2, 2), NumberStyles.HexNumber);
                byte b = byte.Parse(hexColor.Substring(4, 2), NumberStyles.HexNumber);

                // If 8-digit hex (with alpha), parse alpha value
                if (hexColor.Length == 8)
                {
                    a = byte.Parse(hexColor.Substring(6, 2), NumberStyles.HexNumber);
                }

                return Color.FromArgb(a, r, g, b);
            }
            catch
            {
                return DependencyProperty.UnsetValue;
            }
        }

        return DependencyProperty.UnsetValue;
    }

    private SolidColorBrush GetDefaultBrush()
    {
        // Get current theme's foreground brush.
        // Bug: This should be ThemeResource to react to theme changes, but it's not.
        // So if user changes the dark/light theme, this won't update.

        if (WinoApplication.Current.UnderlyingThemeService.IsUnderlyingThemeDark())
            return new SolidColorBrush(Colors.White);
        else
            return new SolidColorBrush(Colors.Black);
    }
}
