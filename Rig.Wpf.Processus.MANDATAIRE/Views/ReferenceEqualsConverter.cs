using System;
using System.Globalization;
using System.Windows.Data;

namespace Rig.Wpf.Processus.Mandataire.Views;

/// <summary>
/// IMultiValueConverter qui compare 2 références : retourne <c>true</c>
/// si elles pointent vers le même objet. Utilisé pour marquer l'étape
/// courante dans le breadcrumb (compare l'étape de la ligne avec
/// CurrentEtape du processus).
/// </summary>
public sealed class ReferenceEqualsConverter : IMultiValueConverter
{
    public static readonly ReferenceEqualsConverter Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return false;
        return ReferenceEquals(values[0], values[1]);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
