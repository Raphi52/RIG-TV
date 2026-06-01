// SPDX-License-Identifier: Proprietary
// Catalog DCADEMAT — miroir instance-based d'AlertesScenarioCatalog (2026-05-29 soir).
// Construit les tuiles depuis un plan d'instances (BuildDcadematSuitePlan = tuiles DCA + Formalité ;
// BuildDcadematStressPlan = X copies d'un scénario). Play/Stop par-scénario délégués au VM.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Rig.Wpf.Kbis.TestViewer.Services;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels.Smoke.Dcademat;

/// <summary>
/// Spec d'une instance d'un scénario DCADEMAT. Produite par
/// <see cref="MainWindowViewModel.BuildDcadematSuitePlan"/> et consommée par le catalog (tuiles)
/// ET le spawn loop (workers) — même instanceId des 2 côtés.
/// </summary>
public sealed class DcadematInstanceSpec
{
    public DcadematInstanceSpec(ScenarioViewModel baseVm, string arg, string instanceId, int displayIndex, string kind, bool showSuffix)
    {
        BaseVm = baseVm;
        Arg = arg;
        InstanceId = instanceId;
        DisplayIndex = displayIndex;
        Kind = kind;
        ShowSuffix = showSuffix;
    }

    public ScenarioViewModel BaseVm { get; }
    /// <summary>Arg CLI SmokeRunner : --legacy-dcademat-dca-validation / -dca-reclamation / ... / -form-interrompue.</summary>
    public string Arg { get; }
    /// <summary>Identité unique : "dcademat-dca-validation-1"… (workerId + sous-dossier snap).</summary>
    public string InstanceId { get; }
    public int DisplayIndex { get; }
    /// <summary>"dca-validation" | "dca-reclamation" | "dca-refus" | "dca-interrompue" | "form-*".</summary>
    public string Kind { get; }
    public bool ShowSuffix { get; }
}

public sealed class DcadematScenarioCatalog : IScenarioCatalog
{
    private readonly MainWindowViewModel _vm;
    private readonly ObservableCollection<DcadematLegacyScenarioAdapter> _scenarios = new();
    private IScenarioItem? _selectedScenario;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DcadematScenarioCatalog(MainWindowViewModel vm)
    {
        _vm = vm;
        ScenariosView = (ListCollectionView)CollectionViewSource.GetDefaultView(_scenarios);

        // Play/Stop par-scénario (contrat IScenarioCatalog → boutons ▶/⏹ du template MosaicView).
        PlayScenarioCommand = new RelayCommand<object?>(item => _vm.PlayDcadematScenario(item as DcadematLegacyScenarioAdapter));
        StopScenarioCommand = new RelayCommand<object?>(item => _vm.StopDcadematScenario(item as DcadematLegacyScenarioAdapter));

        Rebuild(_vm.BuildDcadematSuitePlan());
        _vm.DcadematLegacyScenarios.CollectionChanged += OnSourceChanged;
    }

    public ICollectionView ScenariosView { get; }

    public IScenarioItem? SelectedScenario
    {
        get => _selectedScenario;
        set
        {
            if (_selectedScenario == value) return;
            _selectedScenario = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedScenario)));
        }
    }

    /// <summary>Reconstruit les tuiles depuis un plan d'instances (suite ou stress).</summary>
    public void Rebuild(IReadOnlyList<DcadematInstanceSpec> plan)
    {
        _scenarios.Clear();
        foreach (var spec in plan)
            _scenarios.Add(new DcadematLegacyScenarioAdapter(
                spec.BaseVm, _vm.LegacySmokeProxy,
                spec.InstanceId, spec.DisplayIndex, spec.Kind, spec.ShowSuffix));
        SelectedScenario = _scenarios.Count > 0 ? _scenarios[0] : null;
    }

    public void ResetAllFrozenPaths()
    {
        foreach (var a in _scenarios) a.ResetFrozenPath();
    }

    public void NotifyAllSnapChanged()
    {
        foreach (var a in _scenarios) a.NotifySnapChanged();
    }

    public IReadOnlyList<DcadematLegacyScenarioAdapter> Items => _scenarios;

    public ICommand PlayScenarioCommand { get; }
    public ICommand StopScenarioCommand { get; }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Rebuild(_vm.BuildDcadematSuitePlan());
}
