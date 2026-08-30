using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SteamSwitchboard.Converters;

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            return value is string hex
                ? new BrushConverter().ConvertFromString(hex) as Brush ?? Brushes.DodgerBlue
                : Brushes.DodgerBlue;
        }
        catch (FormatException)
        {
            return Brushes.DodgerBlue;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
