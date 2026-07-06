using System.Globalization;
using System.Windows.Data;

namespace ReDows.Gui.Navigation;

/// <summary>
/// True when the bound string equals the converter parameter (ordinal, case-insensitive). Used by the
/// nav items to light up the one matching the current screen.
/// </summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
