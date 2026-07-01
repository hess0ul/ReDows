using System.Globalization;
using System.Windows.Data;

namespace ReDows.Gui.Navigation;

/// <summary>Inverts a bool for two-way bindings (e.g. the second radio: "a folder" = not "whole PC").</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is bool b ? !b : false;
}
