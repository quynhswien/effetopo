using System;
using System.Globalization;
using System.Windows.Data;

namespace effetopo.Views.Converters
{
    public class NumberToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int intValue)
            {
                return intValue > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            }

            if (value is double doubleValue)
            {
                return doubleValue > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            }

            return System.Windows.Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}