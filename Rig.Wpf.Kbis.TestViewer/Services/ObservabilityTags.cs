using System;
using System.Text.RegularExpressions;

namespace Rig.Wpf.Kbis.TestViewer.Services;

/// <summary>
/// Helpers PURS de parsing/format des tags d'observabilité côté TestViewer (#6 consomme #2) :
/// extrait <c>[snap=NNNN]</c> et <c>[hh:mm:ss.fff]</c> des lignes stdout du worker, et formate la
/// ligne phases.jsonl. Extraits ici pour être testables en xUnit (le reste de #6 est de l'I/O glue).
/// </summary>
public static class ObservabilityTags
{
    private static readonly Regex SnapRe = new Regex(@"\[snap=(\d+)\]", RegexOptions.Compiled);
    private static readonly Regex TimeRe = new Regex(@"\[(\d{2}:\d{2}:\d{2}\.\d{3})\]", RegexOptions.Compiled);

    /// <summary><c>[snap=NNNN]</c> → index entier ; absent ou <c>[snap=----]</c> → -1.</summary>
    public static int ParseSnapTag(string line)
    {
        if (string.IsNullOrEmpty(line)) return -1;
        var m = SnapRe.Match(line);
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : -1;
    }

    /// <summary><c>[hh:mm:ss.fff]</c> → la chaîne horaire ; absent → <paramref name="fallback"/>.</summary>
    public static string ParseTimeTag(string line, string fallback)
    {
        if (!string.IsNullOrEmpty(line))
        {
            var m = TimeRe.Match(line);
            if (m.Success) return m.Groups[1].Value;
        }
        return fallback;
    }

    /// <summary>Ligne JSONL de phases : objet {ts, phase, snap_idx} sur une ligne.</summary>
    public static string PhaseLine(string ts, string phase, int snapIdx) =>
        $"{{\"ts\":\"{ts}\",\"phase\":\"{phase}\",\"snap_idx\":{snapIdx}}}";
}
