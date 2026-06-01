// BoolToVisibilityInvertedConverter — true → Collapsed, false → Visible.
// Utilisé par MainWindow.xaml (Smoke Import tab) pour masquer mosaïque/liste/footer
// quand FocusIsFullscreen=true.
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Rig.Wpf.Kbis.TestViewer.Converters
{
    public sealed class BoolToVisibilityInvertedConverter : IValueConverter
    {
        /// <summary>Singleton instance utilisable via x:Static depuis n'importe quel scope XAML
        /// (sans avoir à le déclarer dans Resources). Pattern aligné avec BoolToVisibilityConverter
        /// et InverseBoolConverter qui exposent aussi `.Instance`.</summary>
        public static readonly BoolToVisibilityInvertedConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
