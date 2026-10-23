using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ExitBoost.App.Converters;

/// <summary>Преобразует строку вида #RRGGBB в кисть.</summary>
public sealed class StringToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string? s = value as string;
        if (string.IsNullOrWhiteSpace(s)) return Brushes.Gray;
        try
        {
            var converted = ColorConverter.ConvertFromString(s);
            return converted is Color color ? new SolidColorBrush(color) : Brushes.Gray;
        }
        catch
        {
            return Brushes.Gray;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
