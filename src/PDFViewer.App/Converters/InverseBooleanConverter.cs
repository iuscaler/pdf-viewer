using System.Globalization;
using System.Windows.Data;

namespace PDFViewer.App.Converters;

/// <summary>bool 값을 뒤집는다.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not true;
}
