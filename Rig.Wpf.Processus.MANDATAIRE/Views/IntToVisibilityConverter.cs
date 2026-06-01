using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Rig.Wpf.Processus.Mandataire.Views;

/// <summary>
/// Converter utilitaire : masque (Collapsed) un visual quand sa source int = 0.
/// Pratique pour les badges "id #N" qui n'ont de sens qu'après sauvegarde.
/// </summary>
public sealed class IntToVisibilityConverter : IValueConverter
{
    public static readonly IntToVisibilityConverter WhenZeroCollapsed = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int i && i == 0) return Visibility.Collapsed;
        if (value is null) return Visibility.Collapsed;
        return Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
