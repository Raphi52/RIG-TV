using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Rig.Wpf.Processus.Mandataire.Views;

/// <summary>
/// StatusKind ("success"/"warning"/"error"/autre) → Brush (Background ou Foreground)
/// pour la pill de feedback du footer Save.
/// </summary>
public sealed class StatusKindToBrushConverter : IValueConverter
{
    public static readonly StatusKindToBrushConverter Background = new(isForeground: false);
    public static readonly StatusKindToBrushConverter Foreground = new(isForeground: true);

    private readonly bool _isForeground;
    private StatusKindToBrushConverter(bool isForeground) => _isForeground = isForeground;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var kind = (value as string ?? string.Empty).ToLowerInvariant();
        return (kind, _isForeground) switch
        {
            ("success", true)  => Brush(0x1E, 0x8E, 0x5A),
            ("success", false) => Brush(0xDD, 0xF3, 0xE8),
            ("warning", true)  => Brush(0xB5, 0x78, 0x1A),
            ("warning", false) => Brush(0xFB, 0xEB, 0xC9),
            ("error",   true)  => Brush(0xC0, 0x39, 0x2B),
            ("error",   false) => Brush(0xF8, 0xDA, 0xD5),
            (_, true)          => Brush(0x2C, 0x6F, 0xB0),
            (_, false)         => Brush(0xDC, 0xE9, 0xF6),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush Brush(byte r, byte g, byte b) =>
        new(Color.FromRgb(r, g, b));
}
