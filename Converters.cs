using System;
using System.Globalization;
using System.Windows.Data;

namespace MajesticParser;

// true -> false и наоборот (для IsEnabled у [MISSING] тредов)
public class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
