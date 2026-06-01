using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Rig.Wpf.Kbis.TestViewer.Converters;

/// <summary>
/// Returns Visibility.Collapsed when the string is null or empty, Visible otherwise.
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
