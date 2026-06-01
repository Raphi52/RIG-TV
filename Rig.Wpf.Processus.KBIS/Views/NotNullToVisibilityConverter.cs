using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Rig.Wpf.Processus.Kbis.Views;

/// <summary>
/// Object non-null/non-vide → Visible, sinon Collapsed. Utilisé pour masquer
/// la toolbar PDF tant qu'aucun PDF n'est chargé.
/// </summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public static readonly NotNullToVisibilityConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return Visibility.Collapsed;
        if (value is string s && string.IsNullOrWhiteSpace(s)) return Visibility.Collapsed;
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
