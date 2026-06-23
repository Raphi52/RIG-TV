using System;
using System.Globalization;
using System.Windows.Data;

namespace Rig.Wpf.Kbis.TestViewer.Converters;

/// <summary>
/// Largeur disponible (ActualWidth de la ListBox) → nombre de colonnes de la mosaïque.
/// = max(MinColumns, floor(width / cibleTuile)). Garantit ≥2 colonnes même dans la colonne
/// latérale étroite (~420px en mode 3-col), et en ajoute à mesure que la largeur croît
/// (mode empilé pleine largeur). Remplace la largeur de tuile FIXE (210px) qui ne tenait
/// qu'UNE colonne dans la colonne latérale (régression vécue 2026-06-23 : « j'ai plus qu'une
/// colonne »). ConverterParameter = largeur cible d'une tuile en px (défaut 195) ;
/// MinColumns = 2.
/// </summary>
public sealed class WidthToColumnsConverter : IValueConverter
{
    public const int MinColumns = 2;
    public const double DefaultTileWidth = 195d;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double width || double.IsNaN(width) || width <= 0)
            return MinColumns;

        var tile = DefaultTileWidth;
        if (parameter is string s &&
            double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) && p > 0)
        {
            tile = p;
        }

        var cols = (int)Math.Floor(width / tile);
        return Math.Max(MinColumns, cols);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
