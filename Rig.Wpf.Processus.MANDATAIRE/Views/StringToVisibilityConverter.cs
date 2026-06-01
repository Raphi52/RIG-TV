using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Rig.Wpf.Processus.Mandataire.Views;

/// <summary>String non-vide → Visible, sinon Collapsed.</summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public static readonly StringToVisibilityConverter WhenEmptyCollapsed = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
