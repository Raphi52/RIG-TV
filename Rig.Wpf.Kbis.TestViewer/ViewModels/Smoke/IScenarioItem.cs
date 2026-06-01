// SPDX-License-Identifier: Proprietary
// SmokePage template — interfaces partagées (Phase 1).

using System.Collections.Generic;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels.Smoke;

/// <summary>
/// Un scénario individuel exposé aux vues (MosaicView tile / FocusView zoom /
/// ScenarioListView ligne grid). Read-only côté UI ; les changements (Phase,
/// Verdict, LogsTail, LastSnapPath) viennent du worker via l'implémentation
/// (ScenarioRunState ObservableObject pour Rapture, KbisScenarioState pour KBIS, etc.).
///
/// Toute concrétion qui implémente cette interface doit raiser PropertyChanged
/// sur les props observables — sinon les bindings WPF ne réagissent pas aux updates.
/// </summary>
public interface IScenarioItem
{
    /// <summary>Identifiant stable du scénario (ex. "cas-a-subset"). Sert de clé
    /// pour les caches, les AutomationId, et le tri.</summary>
    string Id { get; }

    /// <summary>Phase courante (Queued → Launching → ... → Done/Fail). Drive le
    /// SortKey + l'affichage status dans la mosaïque.</summary>
    ScenarioPhase Phase { get; }

    /// <summary>Verdict final (Pending pendant l'exécution, Pass/Fail/Flaky à la fin).
    /// Drive la couleur du border de tile + le banner overlay PNG.</summary>
    Verdict Verdict { get; }

    /// <summary>PID du worker (RigClientAccueil + SmokeRunner) quand le scénario tourne ;
    /// null avant Launching ou après Kill. Affiché dans le header focus + utilisé pour
    /// kill cibled depuis le bouton ✕ Close HDESK.</summary>
    int? WorkerPid { get; }

    /// <summary>Nom du Desktop Windows (HDESK) isolé où tourne le worker — null si
    /// mode in-process. Drive l'enable du bouton "↗ Ouvrir HDESK" (présent → enabled).</summary>
    string? DesktopName { get; }

    /// <summary>Buffer rolling des dernières N=500 lignes de log du worker. Affiché
    /// dans le RowDetailsTemplate de ScenarioListView + extrait par LogsTailToFailReasonConverter
    /// pour le banner verdict (Exception: ... finale).</summary>
    string LogsTail { get; }

    /// <summary>Chemin absolu du PNG de l'état UI courant (self-snap). Null tant que
    /// le worker n'a pas écrit son premier snap. Affiché dans MosaicView tile (130px)
    /// + FocusView (Viewbox plein). PathToCachedBitmapConverter sécurise le chargement.</summary>
    string? LastSnapPath { get; }

    /// <summary>Chemin du screenshot capturé au moment du FAIL (full window). Null si
    /// PASS ou pas encore terminé. Affiché dans le RowDetailsTemplate pour debug visuel
    /// immédiat sans HDESK switch.</summary>
    string? FailScreenshotPath { get; }

    /// <summary>Durée formatée mm:ss directement bindable (pas de StringFormat XAML car
    /// backslash escape `mm\:ss` parse mal en MultiBinding). "—:—" si pas démarré.</summary>
    string DurationFormatted { get; }

    /// <summary>True si le bouton ▶ Play est visible/enabled (état Queued OU re-run
    /// après Done/Fail). Drive la Visibility du Button ▶ dans la tile mosaïque.</summary>
    bool CanPlay { get; }

    /// <summary>True si le bouton ⏹ Stop est visible/enabled (worker actif).
    /// Drive la Visibility du Button ⏹ dans la tile mosaïque.</summary>
    bool CanStop { get; }

    /// <summary>
    /// Steps internes du scenario avec leur status (Passed/Failed/Pending), affichés
    /// dans la FocusView ("STEP 1 ✓ Passed" / "STEP 4 ⏱ Pending"). Construits par fusion
    /// expected-vs-actual : les steps pré-définis dans l'adapter sont mappés aux lignes
    /// ✓/✗ reçues, les non-reçus restent en Pending visible (point clé vs SmokeResultLine
    /// qui n'apparaît qu'après réception). Default = empty list (Rapture, etc.).
    /// </summary>
    IReadOnlyList<StepDisplay> Steps { get; }
}
