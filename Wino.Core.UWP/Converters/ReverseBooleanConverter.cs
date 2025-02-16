using System;
using Windows.UI.Xaml.Data;

namespace Wino.Converters;

public partial class ReverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is bool boolval)
            return !boolval;

        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
