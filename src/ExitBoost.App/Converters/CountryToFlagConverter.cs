using System.Globalization;
using System.Windows.Data;
using ExitBoost.Core.Vpn;

namespace ExitBoost.App.Converters;

/// <summary>Название страны → флаг-эмодзи («Германия» → 🇩🇪).</summary>
public sealed class CountryToFlagConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => VpnCountry.FlagFor(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
