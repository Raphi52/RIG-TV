using System;
using System.Globalization;
using System.Windows.Data;

namespace Rig.Wpf.Kbis.TestViewer.Converters;

/// <summary>
/// Returns true when value is non-null, false otherwise. Utilisé pour IsEnabled sur les boutons
/// qui dépendent qu'un scénario soit sélectionné (BatchState.SelectedScenario.DesktopName).
/// </summary>
public sealed class NullToBoolInvertedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value != null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
