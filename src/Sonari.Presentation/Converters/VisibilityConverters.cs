using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Sonari.Presentation.Converters;

/// <summary>Instancias compartidas para usar desde XAML con <c>{x:Static conv:VisibilityConverters.NotNull}</c>.</summary>
public static class VisibilityConverters
{
    /// <summary>Visible si el valor no es null ni una cadena vacía.</summary>
    public static IValueConverter NotNull { get; } = new DelegateConverter(value =>
        value is null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible);

    /// <summary>Visible si el valor es el entero 0 (para estados vacíos de listas).</summary>
    public static IValueConverter Zero { get; } = new DelegateConverter(value =>
        value is 0 ? Visibility.Visible : Visibility.Collapsed);

    private sealed class DelegateConverter(Func<object?, Visibility> convert) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => convert(value);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
