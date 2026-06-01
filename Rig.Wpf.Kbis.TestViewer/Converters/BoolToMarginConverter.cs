using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Rig.Wpf.Kbis.TestViewer.Converters;

/// <summary>
/// bool → Thickness pour les Margins toggleables par état (e.g. FocusIsFullscreen).
/// True → Thickness(0). False → Thickness parsée depuis parameter (e.g. "22,12,22,22") ou Thickness(22) défaut.
/// </summary>
public sealed class BoolToMarginConverter : IValueConverter
{
    /// <summary>Singleton instance utilisable via x:Static depuis n'importe quel scope XAML.</summary>
    public static readonly BoolToMarginConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool x && x;
        if (b) return new Thickness(0);
        if (parameter is string s && !string.IsNullOrEmpty(s))
        {
            var parts = s.Split(',');
            if (parts.Length == 4 &&
                double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var l) &&
                double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var t) &&
                double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var r) &&
                double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out var bot))
                return new Thickness(l, t, r, bot);
        }
        return new Thickness(22, 12, 22, 22);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
