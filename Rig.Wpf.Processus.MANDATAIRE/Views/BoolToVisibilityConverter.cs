using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Rig.Wpf.Processus.Mandataire.Views;

/// <summary>
/// Converter bool → Visibility. <c>true</c> = Visible, <c>false</c> = Collapsed.
/// Si <see cref="Inverse"/> = true, la logique est inversée.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Default = new() { Inverse = false };
    public static readonly BoolToVisibilityConverter Inverted = new() { Inverse = true };

    public bool Inverse { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isTrue = value is bool b && b;
        if (Inverse) isTrue = !isTrue;
        return isTrue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
