using System.Globalization;
using System.Windows.Data;
using PaintPro.Models;

namespace PaintPro.Converters;

/// <summary>True when the bound ToolKind equals the parameter (parsed as ToolKind).</summary>
public sealed class ToolKindToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ToolKind v && parameter is string s && Enum.TryParse<ToolKind>(s, out var p))
            return v == p;
        return false;
    }

    /// <summary>Reverse path: when a ToggleButton is checked it sets ActiveTool to the parameter value.</summary>
    public object? ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter is string s && Enum.TryParse<ToolKind>(s, out var p))
            return p;
        return Binding.DoNothing;
    }
}
