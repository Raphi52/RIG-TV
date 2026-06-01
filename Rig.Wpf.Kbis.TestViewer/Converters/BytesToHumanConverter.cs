using System;
using System.Globalization;
using System.Windows.Data;

namespace Rig.Wpf.Kbis.TestViewer.Converters;

/// <summary>
/// long bytes → "12.3 MB" / "1.2 GB" pour affichage compact (drawer Historique).
/// </summary>
public sealed class BytesToHumanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long b)
        {
            var mb = b / 1024.0 / 1024.0;
            return mb < 1024 ? $"{mb:F1} MB" : $"{mb / 1024:F1} GB";
        }
        return "0 B";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
