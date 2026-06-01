using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Rig.Wpf.Kbis.TestViewer.ViewModels;

namespace Rig.Wpf.Kbis.TestViewer.Converters;

/// <summary>
/// Verdict → SolidColorBrush pour les cellules Verdict du DataGrid + tiles mosaïque.
/// Pass = vert (#00AA00) / Fail = rouge (#CC0000) / Flaky = orange (#FF9900) / défaut = gris (#888888).
/// </summary>
public sealed class VerdictToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Verdict.Pass => new SolidColorBrush(Color.FromRgb(0, 170, 0)),
        Verdict.Fail => new SolidColorBrush(Color.FromRgb(204, 0, 0)),
        Verdict.Flaky => new SolidColorBrush(Color.FromRgb(255, 153, 0)),
        _ => new SolidColorBrush(Color.FromRgb(136, 136, 136)),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
