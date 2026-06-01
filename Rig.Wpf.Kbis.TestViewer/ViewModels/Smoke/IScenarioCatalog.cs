// SPDX-License-Identifier: Proprietary
// SmokePage template — interface catalogue scénarios (Phase 1).

using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels.Smoke;

/// <summary>
/// Catalogue des scénarios courants (états runtime) + sélection partagée +
/// commandes Play/Stop par scénario.
///
/// Utilisé par :
///   - MosaicView (ListBox ItemsSource = ScenariosView, SelectedItem = SelectedScenario)
///   - ScenarioListView (DataGrid ItemsSource = ScenariosView)
///   - FocusView indirectement (via IFocusContext.SelectedItem qui reflète SelectedScenario)
///
/// L'implémentation tient une ObservableCollection&lt;ScenarioRunState&gt; en interne ;
/// ScenariosView est une ListCollectionView dérivée (tri/filtre). Les éléments retournés
/// par le runtime XAML implémentent IScenarioItem (cast implicite via le runtime DataTemplate).
/// </summary>
public interface IScenarioCatalog : INotifyPropertyChanged
{
    /// <summary>Vue triée/filtrée des scénarios pour binding XAML (ListBox.ItemsSource,
    /// DataGrid.ItemsSource). Les items implémentent IScenarioItem.</summary>
    ICollectionView ScenariosView { get; }

    /// <summary>Scénario sélectionné. Bind TwoWay sur ListBox.SelectedItem +
    /// DataGrid.SelectedItem. Drive IFocusContext.SelectedItem.</summary>
    IScenarioItem? SelectedScenario { get; set; }

    /// <summary>Lance le scénario passé en CommandParameter (un IScenarioItem).
    /// Bound au bouton ▶ Play par tile mosaïque + bouton ▶ Run all global filtré.</summary>
    ICommand PlayScenarioCommand { get; }

    /// <summary>Stoppe le worker du scénario passé en CommandParameter (taskkill /T /F).
    /// Bound au bouton ⏹ Stop par tile mosaïque.</summary>
    ICommand StopScenarioCommand { get; }
}
