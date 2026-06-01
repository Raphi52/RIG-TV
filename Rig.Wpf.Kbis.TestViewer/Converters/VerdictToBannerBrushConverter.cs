using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Rig.Wpf.Kbis.TestViewer.ViewModels;

namespace Rig.Wpf.Kbis.TestViewer.Converters;

/// <summary>
/// Verdict → SolidColorBrush pour le BACKGROUND du bandeau verdict overlay sur le Focus PNG.
/// Pass = vert (#28A745) / Fail = rouge (#DC3545) / Flaky = orange (#FF9900) / Pending = transparent
/// (le bandeau ne doit pas apparaître pour Pending — couplé avec VerdictToBannerVisibilityConverter).
/// </summary>
public sealed class VerdictToBannerBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Verdict.Pass => new SolidColorBrush(Color.FromRgb(0x28, 0xA7, 0x45)),
        Verdict.Fail => new SolidColorBrush(Color.FromRgb(0xDC, 0x35, 0x45)),
        Verdict.Flaky => new SolidColorBrush(Color.FromRgb(0xFF, 0x99, 0x00)),
        _ => System.Windows.Media.Brushes.Transparent,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
