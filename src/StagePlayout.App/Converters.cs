using System.Globalization;
using System.Windows.Data;

namespace StagePlayout.App;

/// <summary>
/// Converte valor &lt;-&gt; bool comparando com o ConverterParameter.
/// Usado nos submenus de fade (comportamento "radio" via IsChecked).
/// </summary>
public class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Enum e)
            return string.Equals(e.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

        if (value is double d &&
            double.TryParse(parameter?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
            return d == p;
        return Equals(value, parameter);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        // só o "check" escreve o valor; o "uncheck" é ignorado (radio behavior)
        if (value is bool b && !b)
            return Binding.DoNothing;

        if (targetType.IsEnum)
            return Enum.Parse(targetType, parameter?.ToString() ?? "");

        if (double.TryParse(parameter?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
            return p;
        return parameter;
    }
}
