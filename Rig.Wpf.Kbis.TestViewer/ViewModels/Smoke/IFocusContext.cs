// SPDX-License-Identifier: Proprietary
// SmokePage template — interface FocusView (Phase 1).

using System.ComponentModel;
using System.Windows.Input;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels.Smoke;

/// <summary>
/// State + actions pour la zone PNG centrale (FocusView).
/// Bindings côté XAML :
///   - SelectedItem.* → props du scénario zoomé (Id, Phase, Verdict, LastSnapPath, etc.)
///   - IsFullscreen → toggle plein écran (cache mosaic + list, étend la col centrale)
///   - IsRenderingPaused / SnapRefreshIntervalMs → toolbar LIVE controls
///   - CurrentRunStamp → label "Run : XXX" dans la toolbar
///   - ToggleFullscreen / OpenHdesk / CloseHdesk / OpenSnapsRoot / OpenSnapsFolder / CopyLogs → boutons
///
/// L'implémentation doit raiser PropertyChanged sur SelectedItem, IsFullscreen,
/// IsRenderingPaused, SnapRefreshIntervalMs, CurrentRunStamp — sinon les bindings
/// ne réagissent pas aux changements (mosaic select, fullscreen toggle, etc.).
/// </summary>
public interface IFocusContext : INotifyPropertyChanged
{
    /// <summary>Scénario actuellement zoomé (sélectionné dans la mosaïque ou la liste).
    /// Null = état initial "Sélectionne un scénario".</summary>
    IScenarioItem? SelectedItem { get; }

    /// <summary>True quand la vue est en plein écran (overlay YouTube-like, mosaic + list cachées).
    /// Toggled par ToggleFullscreenCommand.</summary>
    bool IsFullscreen { get; }

    /// <summary>Pause du timer de rafraîchissement des PNG self-snap. Toggle bouton ⏸ Pause.
    /// Quand true, les LastSnapPath des scénarios ne sont plus mis à jour côté UI.</summary>
    bool IsRenderingPaused { get; set; }

    /// <summary>Intervalle en ms du timer de rafraîchissement (slider Refresh, 100-2000 ms).
    /// Modifié via le slider de la toolbar LIVE.</summary>
    int SnapRefreshIntervalMs { get; set; }

    /// <summary>RUN_STAMP courant (yyyyMMdd-HHmmss). Affiché dans la toolbar pour
    /// localiser visuellement le dossier des snaps du batch en cours.</summary>
    string? CurrentRunStamp { get; }

    /// <summary>Toggle l'état IsFullscreen. Bound aux 2 boutons "⛶ Plein écran" / "⛶ Réduire"
    /// (mode normal + overlay fullscreen).</summary>
    ICommand ToggleFullscreenCommand { get; }

    /// <summary>Switch sur le Desktop Windows isolé du scénario sélectionné pour voir
    /// RigClientAccueil en live. Enabled uniquement si SelectedItem.DesktopName non-null.
    /// Escape via Ctrl+Alt+Backspace (HDeskEscapeHelper).</summary>
    ICommand OpenHdeskCommand { get; }

    /// <summary>Kill le worker du scénario sélectionné + libère son HDESK.
    /// Bound au bouton ✕ rouge dans la toolbar LIVE.</summary>
    ICommand CloseHdeskCommand { get; }

    /// <summary>Ouvre Explorer sur le dossier racine des self-snaps (tous runs confondus).
    /// Bound au bouton 📁 Snaps root de la toolbar LIVE.</summary>
    ICommand OpenSnapsRootCommand { get; }

    /// <summary>Ouvre Explorer sur le dossier de snaps du scénario sélectionné UNIQUEMENT.
    /// Bound au bouton 📁 Snaps folder de l'action bar (mode normal).</summary>
    ICommand OpenSnapsFolderCommand { get; }

    /// <summary>Copie les LogsTail du scénario sélectionné dans le presse-papier.
    /// Bound au bouton 📋 Copier logs.</summary>
    ICommand CopyLogsCommand { get; }
}
