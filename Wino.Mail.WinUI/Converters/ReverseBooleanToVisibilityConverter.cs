using System;


#if NET8_0
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml;
#else
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml;
#endif

namespace Wino.Converters
{
    public class ReverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return ((bool)value) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
