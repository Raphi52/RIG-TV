// SPDX-License-Identifier: Proprietary
// SmokePage template — interface top-level page (Phase 1).

using System.ComponentModel;
using System.Windows.Input;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels.Smoke;

/// <summary>
/// ViewModel top-level pour SmokePageView (template réutilisable).
/// Agrège les 4 sous-VM (Controls / Catalog / Focus / Footer) + le titre de la page.
///
/// Chaque module (Rapture, KBIS, IPE, MANDATAIRE) implémente cette interface dans son
/// propre {Module}SmokePageViewModel. SmokePageView.xaml bind un seul DataContext —
/// ce VM — et propage aux composants enfants via les sous-props.
///
/// Pattern :
///   &lt;views:SmokePageView DataContext="{Binding RaptureSmokePageViewModel}" /&gt;
///   &lt;views:SmokePageView DataContext="{Binding KbisSmokePageViewModel}" /&gt;
///
/// SmokePageView.xaml interne :
///   &lt;views:BatchControlsView DataContext="{Binding Controls}" /&gt;
///   &lt;views:MosaicView DataContext="{Binding Catalog}" /&gt;
///   &lt;views:FocusView DataContext="{Binding Focus}" /&gt;
///   &lt;views:ScenarioListView DataContext="{Binding Catalog}" /&gt;
///   &lt;views:FooterView DataContext="{Binding Footer}" /&gt;
/// </summary>
public interface ISmokePageViewModel : INotifyPropertyChanged
{
    /// <summary>Titre principal de la page (ex. "Smoke Import — sélectionner un scénario").
    /// Affiché en haut du Card header.</summary>
    string PageTitle { get; }

    /// <summary>Bandeau d'actions batch (Card header).</summary>
    IBatchControls Controls { get; }

    /// <summary>Catalogue runtime des scénarios (Mosaic + List).</summary>
    IScenarioCatalog Catalog { get; }

    /// <summary>Contexte de la vue focus (PNG zoom).</summary>
    IFocusContext Focus { get; }

    /// <summary>Footer disk usage.</summary>
    IFooter Footer { get; }

    /// <summary>True quand le batch tourne (Run all en cours). Drive CanExecute des
    /// commandes batch + grise certains contrôles. Forwarded de IBatchControls
    /// pour éviter à la View de traverser 2 niveaux de binding.</summary>
    bool IsBatchRunning { get; }

    /// <summary>Compte PASS / FAIL / Queued / Running pour le badge en haut à droite
    /// du SmokePageView (StatusPillOk style "✓N ✗M ⊘K").</summary>
    int PassCount { get; }
    int FailCount { get; }
    int QueuedCount { get; }
    int RunningCount { get; }
}
