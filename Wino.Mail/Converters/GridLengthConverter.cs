using System;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml;

namespace Wino.Converters
{
    public class GridLengthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is double doubleValue)
            {
                return new GridLength(doubleValue);
            }
            return new GridLength(1, GridUnitType.Auto);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is GridLength gridLength)
            {
                return gridLength.Value;
            }
            return 0.0;
        }
    }
}
