using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;

namespace Rig.Wpf.Kbis.TestViewer.Converters
{
    /// <summary>MultiBinding : Visible si AU MOINS UN des bool fournis est true, sinon Collapsed.
    /// Sert à collapser un segment ribbon entier quand aucune de ses capacités n'est active
    /// (ex. CONTRÔLE masqué si ni ShowPause ni ShowStop).</summary>
    public sealed class AnyTrueToVisibilityConverter : IMultiValueConverter
    {
        public static readonly AnyTrueToVisibilityConverter Instance = new AnyTrueToVisibilityConverter();

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            => (values != null && values.Any(v => v is bool b && b)) ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
