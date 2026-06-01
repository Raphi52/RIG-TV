// SPDX-License-Identifier: Proprietary
// SmokePage template — adapter Rapture pour IScenarioCatalog (Phase 4).

using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels.Smoke.Rapture;

/// <summary>
/// Implémente <see cref="IScenarioCatalog"/> en wrappant les props/commands existants
/// de <see cref="MainWindowViewModel"/> pour MosaicView + ScenarioListView.
///
/// Mapping :
///   MainWindowViewModel.ScenariosView          → IScenarioCatalog.ScenariosView (même nom)
///   MainWindowViewModel.BatchState.SelectedScenario → IScenarioCatalog.SelectedScenario
///   MainWindowViewModel.PlayScenarioCommand    → IScenarioCatalog.PlayScenarioCommand
///   MainWindowViewModel.StopScenarioCommand    → IScenarioCatalog.StopScenarioCommand
///
/// SelectedScenario est read-write : binding TwoWay des ListBox/DataGrid passe par ici.
/// La propagation au underlying BatchRunState.SelectedScenario se fait via le setter.
/// </summary>
public sealed class RaptureScenarioCatalog : IScenarioCatalog
{
    private readonly MainWindowViewModel _vm;

    public event PropertyChangedEventHandler? PropertyChanged;

    public RaptureScenarioCatalog(MainWindowViewModel vm)
    {
        _vm = vm;
        _vm.BatchState.PropertyChanged += OnBatchStatePropertyChanged;
    }

    public ICollectionView ScenariosView => _vm.ScenariosView;

    /// <summary>
    /// Forward TwoWay vers BatchRunState.SelectedScenario. Le set accepte IScenarioItem
    /// — ScenarioRunState implémente IScenarioItem, donc cast direct.
    /// </summary>
    public IScenarioItem? SelectedScenario
    {
        get => _vm.BatchState.SelectedScenario;
        set => _vm.BatchState.SelectedScenario = value as ScenarioRunState;
    }

    public ICommand PlayScenarioCommand => _vm.PlayScenarioCommand;
    public ICommand StopScenarioCommand => _vm.StopScenarioCommand;

    /// <summary>
    /// Pont : re-raise IScenarioCatalog.SelectedScenario quand BatchRunState.SelectedScenario change.
    /// </summary>
    private void OnBatchStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BatchRunState.SelectedScenario))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedScenario)));
    }
}
