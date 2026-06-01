using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Rig.Wpf.Kbis.TestViewer.ViewModels;

namespace Rig.Wpf.Kbis.TestViewer.Converters;

/// <summary>
/// Verdict → Visibility pour le bandeau overlay : visible quand Verdict != Pending
/// (scénario terminé en Pass/Fail/Flaky). Collapsed sinon (pendant le run live).
/// </summary>
public sealed class VerdictToBannerVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is Verdict v && v != Verdict.Pending) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
