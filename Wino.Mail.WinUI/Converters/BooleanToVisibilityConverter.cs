using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace Wino.Converters;

public partial class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return ((bool)value) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
