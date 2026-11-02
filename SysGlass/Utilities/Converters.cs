using System.Globalization;
using System.Windows.Data;

namespace SysGlass.Utilities;

/// <summary>값이 ConverterParameter와 같으면 true. 라디오 버튼으로 정수 옵션을 고를 때 사용한다.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(System.Convert.ToString(value, CultureInfo.InvariantCulture),
                      System.Convert.ToString(parameter, CultureInfo.InvariantCulture), StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && int.TryParse(System.Convert.ToString(parameter, CultureInfo.InvariantCulture), out int n)
            ? n
            : Binding.DoNothing;
}

/// <summary>bool을 반전한다.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}