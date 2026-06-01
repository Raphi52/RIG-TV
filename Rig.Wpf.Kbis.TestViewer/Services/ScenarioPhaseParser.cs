using System.Text.RegularExpressions;
using Rig.Wpf.Kbis.TestViewer.ViewModels;

namespace Rig.Wpf.Kbis.TestViewer.Services;

/// <summary>
/// Maps les marqueurs stdout du LegacyDriver / SmokeRunner vers une ScenarioPhase.
/// Source des marqueurs : Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/LegacyDriver.cs +
/// Source/Wpf/Rig.Wpf.Kbis.SmokeRunner/Program.cs (TryStep titles).
/// </summary>
public static class ScenarioPhaseParser
{
    // Ordre : du plus spécifique au moins spécifique pour éviter qu'un Login marker matche aussi un SelectAudience.
    private static readonly (Regex Pattern, ScenarioPhase Phase)[] Rules =
    {
        (new Regex(@"Exception:", RegexOptions.Compiled), ScenarioPhase.Fail),
        (new Regex(@"FAIL", RegexOptions.Compiled), ScenarioPhase.Fail),
        (new Regex(@"Click 'Importer \(", RegexOptions.Compiled), ScenarioPhase.Apply),
        (new Regex(@"Recap\b", RegexOptions.Compiled), ScenarioPhase.Recap),
        (new Regex(@"Click 'Importer Rapture'", RegexOptions.Compiled), ScenarioPhase.ClickImporter),
        (new Regex(@"Sélection audience", RegexOptions.Compiled), ScenarioPhase.SelectAudience),
        (new Regex(@"Open PROC_RETAUD", RegexOptions.Compiled), ScenarioPhase.OpenProcRetaud),
        (new Regex(@"PostMessage SC_MAXIMIZE|App ouverte post-login", RegexOptions.Compiled), ScenarioPhase.Login),
    };

    /// <summary>
    /// Retourne true si la ligne match un marqueur connu, et set <paramref name="phase"/> au mapping.
    /// Sinon retourne false et phase=Queued (default).
    /// </summary>
    public static bool TryParse(string line, out ScenarioPhase phase)
    {
        foreach (var (pattern, p) in Rules)
        {
            if (pattern.IsMatch(line))
            {
                phase = p;
                return true;
            }
        }
        phase = ScenarioPhase.Queued;
        return false;
    }
}
