using Microsoft.UI.Xaml.Data;

namespace HdrImageViewer.Infrastructure;

public sealed class BooleanToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is bool isVisible && isVisible ? 1.0 : 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return value is double opacity && opacity > 0.5;
    }
}
