// SPDX-License-Identifier: Proprietary
// Catalog ALERTES RCS — miroir instance-based de KbisScenarioCatalog (2026-05-29).
// Construit les tuiles depuis un plan d'instances (BuildAlertesSuitePlan = 4 tuiles ;
// BuildAlertesStressPlan = X copies d'un scénario). Play/Stop par-scénario délégués au VM.

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Rig.Wpf.Kbis.TestViewer.Services;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels.Smoke.Alertes;

/// <summary>
/// Spec d'une instance d'un scénario Alertes. Produite par
/// <see cref="MainWindowViewModel.BuildAlertesSuitePlan"/> et consommée par le catalog (tuiles)
/// ET le spawn loop (workers) — même instanceId des 2 côtés.
/// </summary>
public sealed class AlertesInstanceSpec
{
    public AlertesInstanceSpec(ScenarioViewModel baseVm, string arg, string instanceId, int displayIndex, string kind, bool showSuffix)
    {
        BaseVm = baseVm;
        Arg = arg;
        InstanceId = instanceId;
        DisplayIndex = displayIndex;
        Kind = kind;
        ShowSuffix = showSuffix;
    }

    public ScenarioViewModel BaseVm { get; }
    /// <summary>Arg CLI SmokeRunner : --legacy-alertes-int-form / -int-dca / -rec-form / -rec-dca.</summary>
    public string Arg { get; }
    /// <summary>Identité unique : "alertes-int-form-1"… (workerId + sous-dossier snap).</summary>
    public string InstanceId { get; }
    public int DisplayIndex { get; }
    /// <summary>"int-form" | "int-dca" | "rec-form" | "rec-dca".</summary>
    public string Kind { get; }
    public bool ShowSuffix { get; }
}

public sealed class AlertesScenarioCatalog : IScenarioCatalog
{
    private readonly MainWindowViewModel _vm;
    private readonly ObservableCollection<AlertesLegacyScenarioAdapter> _scenarios = new();
    private IScenarioItem? _selectedScenario;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AlertesScenarioCatalog(MainWindowViewModel vm)
    {
        _vm = vm;
        ScenariosView = (ListCollectionView)CollectionViewSource.GetDefaultView(_scenarios);

        // Play/Stop par-scénario (contrat IScenarioCatalog → boutons ▶/⏹ du template MosaicView).
        PlayScenarioCommand = new RelayCommand<object?>(item => _vm.PlayAlertesScenario(item as AlertesLegacyScenarioAdapter));
        StopScenarioCommand = new RelayCommand<object?>(item => _vm.StopAlertesScenario(item as AlertesLegacyScenarioAdapter));

        Rebuild(_vm.BuildAlertesSuitePlan());
        _vm.AlertesLegacyScenarios.CollectionChanged += OnSourceChanged;
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
    public void Rebuild(IReadOnlyList<AlertesInstanceSpec> plan)
    {
        _scenarios.Clear();
        foreach (var spec in plan)
            _scenarios.Add(new AlertesLegacyScenarioAdapter(
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

    public IReadOnlyList<AlertesLegacyScenarioAdapter> Items => _scenarios;

    public ICommand PlayScenarioCommand { get; }
    public ICommand StopScenarioCommand { get; }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Rebuild(_vm.BuildAlertesSuitePlan());
}
