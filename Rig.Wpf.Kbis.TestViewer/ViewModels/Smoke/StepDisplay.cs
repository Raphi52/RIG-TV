// SPDX-License-Identifier: Proprietary
// Step affiché dans la FocusView : "STEP N ✓ Passed" avec état Pending visible
// pour les steps pas encore exécutés (vs SmokeResultLine qui n'existe qu'une fois
// la ligne reçue côté stdout).

using Rig.Wpf.Kbis.TestViewer.Services;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels.Smoke;

/// <summary>
/// Représentation d'un step à afficher dans la FocusView. Construit par fusion
/// entre une liste pré-définie de steps attendus (avec leur ordre + label court)
/// et les SmokeResultLine reçues du worker. Les steps non encore exécutés ont
/// <see cref="Outcome"/>=<see cref="SmokeOutcome.Unknown"/> (icon ⏱ + label "Pending").
/// </summary>
public sealed class StepDisplay
{
    /// <summary>Numéro du step (1-based) pour affichage "STEP 1", "STEP 2", ...</summary>
    public int StepNumber { get; }

    /// <summary>Label court (e.g., "Sanity", "Login", "Ouvrir PROC_KBIS").</summary>
    public string ShortLabel { get; }

    /// <summary>Outcome du step : Passed/Failed/Skipped si la ligne ✓/✗/⊘ a été
    /// reçue, sinon Unknown (= Pending).</summary>
    public SmokeOutcome Outcome { get; }

    /// <summary>Label textuel pour l'état : "Passed", "Failed", "Skipped", "Pending".</summary>
    public string OutcomeLabel { get; }

    /// <summary>Description complète de la ligne stdout (si reçue), pour tooltip ou
    /// affichage debug. Null si step pas encore exécuté.</summary>
    public string? FullDescription { get; }

    public StepDisplay(int stepNumber, string shortLabel, SmokeOutcome outcome, string? fullDescription)
    {
        StepNumber = stepNumber;
        ShortLabel = shortLabel;
        Outcome = outcome;
        OutcomeLabel = outcome switch
        {
            SmokeOutcome.Passed  => "Passed",
            SmokeOutcome.Failed  => "Failed",
            SmokeOutcome.Skipped => "Skipped",
            _                    => "Pending",
        };
        FullDescription = fullDescription;
    }
}
