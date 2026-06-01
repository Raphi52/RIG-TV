using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace Rig.Wpf.Kbis.TestViewer.Converters;

/// <summary>
/// LogsTail (concat 500 dernières lignes worker) → 1 ligne "raison du FAIL" pour
/// affichage dans le bandeau verdict overlay. Heuristique : on cherche la dernière
/// occurrence de "Exception:" (la plus représentative du root-cause LegacyDriver),
/// sinon fallback sur la dernière ligne ✗ trouvée, sinon string vide.
/// </summary>
public sealed class LogsTailToFailReasonConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string logs || string.IsNullOrEmpty(logs)) return string.Empty;
        var lines = logs.Split('\n');
        // Dernière occurrence d'Exception (root cause LegacyDriver type "OpenFileDialog pas apparu après 30s").
        var exceptionLine = lines.Reverse().FirstOrDefault(l => l.Contains("Exception:"));
        if (exceptionLine != null)
        {
            // Trim le préfixe "    Exception: " pour ne garder que le message.
            var idx = exceptionLine.IndexOf("Exception:", StringComparison.Ordinal);
            return exceptionLine.Substring(idx + "Exception:".Length).Trim();
        }
        // Fallback : dernière ligne ✗ (verdict assertion fail).
        var crossLine = lines.Reverse().FirstOrDefault(l => l.Contains("✗"));
        if (crossLine != null) return crossLine.Trim().TrimStart('✗').Trim();
        return string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
