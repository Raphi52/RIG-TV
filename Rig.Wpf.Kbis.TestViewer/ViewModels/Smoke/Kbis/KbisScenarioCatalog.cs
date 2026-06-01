// SPDX-License-Identifier: Proprietary
// SmokePage template — catalog Kbis (peuplé depuis KbisLegacyScenarios) — 2026-05-28.
//
// 2026-05-29 v3 : INSTANCE-BASED. Le catalog est construit depuis un "plan" d'instances
// (cf. MainWindowViewModel.BuildKbisInstancePlan) qui dépend du parallélisme choisi :
// parallelism=4 → 2 VK + 2 XEX = 4 tuiles indépendantes. Rebuild(plan) reconstruit les
// tuiles quand le parallélisme change (settings) ou au démarrage d'un run.
//
// PlayScenarioCommand par-scénario désactivé (CanPlay=false sur l'adapter) car le smoke
// legacy lance les instances en batch via "▶ Run smoke RIG" (RunLegacyCommand).

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Rig.Wpf.Kbis.TestViewer.Services;

namespace Rig.Wpf.Kbis.TestViewer.ViewModels.Smoke.Kbis;

/// <summary>
/// Spec d'une instance parallèle d'un scenario KBIS. Produite par
/// <see cref="MainWindowViewModel.BuildKbisInstancePlan"/> et consommée à la fois par
/// le catalog (tuiles) et le spawn loop (workers) — même schéma d'instanceId des deux côtés.
/// </summary>
public sealed class KbisInstanceSpec
{
    public KbisInstanceSpec(ScenarioViewModel baseVm, string arg, string instanceId, int displayIndex, bool isVk, bool showSuffix)
    {
        BaseVm = baseVm;
        Arg = arg;
        InstanceId = instanceId;
        DisplayIndex = displayIndex;
        IsVk = isVk;
        ShowSuffix = showSuffix;
    }

    /// <summary>Template du scenario (Title/Description) — VK ou XEX.</summary>
    public ScenarioViewModel BaseVm { get; }
    /// <summary>Arg CLI du worker SmokeRunner : "--legacy-kbis-vk" / "--legacy-kbis-xex".</summary>
    public string Arg { get; }
    /// <summary>Identité unique : "kbis-vk-1", "kbis-xex-2"… (workerId + sous-dossier snap).</summary>
    public string InstanceId { get; }
    /// <summary>Numéro affiché (#1, #2…).</summary>
    public int DisplayIndex { get; }
    public bool IsVk { get; }
    /// <summary>true si plusieurs instances du même type → affiche "#idx" dans le titre.</summary>
    public bool ShowSuffix { get; }
}

public sealed class KbisScenarioCatalog : IScenarioCatalog
{
    private readonly MainWindowViewModel _vm;
    private readonly ObservableCollection<KbisLegacyScenarioAdapter> _scenarios = new();
    private IScenarioItem? _selectedScenario;

    public event PropertyChangedEventHandler? PropertyChanged;

    public KbisScenarioCatalog(MainWindowViewModel vm)
    {
        _vm = vm;
        ScenariosView = (ListCollectionView)CollectionViewSource.GetDefaultView(_scenarios);

        // Play/Stop PAR-SCÉNARIO (contrat IScenarioCatalog → boutons ▶/⏹ du template MosaicView,
        // identiques à RAPTURE). Play = (re)lance CE scénario seul (plan 1-instance) ; Stop =
        // taskkill le worker de la tuile. Délégué au VM qui possède RunKbisPlanAsync + les flags.
        PlayScenarioCommand = new RelayCommand<object?>(item => _vm.PlayKbisScenario(item as KbisLegacyScenarioAdapter));
        StopScenarioCommand = new RelayCommand<object?>(item => _vm.StopKbisScenario(item as KbisLegacyScenarioAdapter));

        // Construit les tuiles depuis le plan SUITE (1 VK + 1 XEX). Le stress reconstruit
        // ensuite via Rebuild(stressPlan) avec X copies du scénario sélectionné.
        Rebuild(_vm.BuildKbisSuitePlan());

        // Re-sync si la source manifest change (reload).
        _vm.KbisLegacyScenarios.CollectionChanged += OnSourceChanged;
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

    /// <summary>
    /// Reconstruit les tuiles depuis un plan d'instances. Appelé : (1) au boot, (2) quand le
    /// parallélisme change (settings), (3) au démarrage d'un run (pour que les tuiles matchent
    /// exactement les workers qui vont être spawnés). Re-sélectionne la 1ère tuile pour que la
    /// FocusView affiche quelque chose.
    /// </summary>
    public void Rebuild(IReadOnlyList<KbisInstanceSpec> plan)
    {
        _scenarios.Clear();
        foreach (var spec in plan)
            _scenarios.Add(new KbisLegacyScenarioAdapter(
                spec.BaseVm, _vm.LegacySmokeProxy,
                spec.InstanceId, spec.DisplayIndex, spec.IsVk, spec.ShowSuffix));

        SelectedScenario = _scenarios.Count > 0 ? _scenarios[0] : null;
    }

    /// <summary>Reset le frozen path de toutes les tuiles (nouveau run smoke).</summary>
    public void ResetAllFrozenPaths()
    {
        foreach (var a in _scenarios) a.ResetFrozenPath();
    }

    /// <summary>Notifie toutes les tuiles que les snaps/HDESK ont pu changer (snap timer tick).</summary>
    public void NotifyAllSnapChanged()
    {
        foreach (var a in _scenarios) a.NotifySnapChanged();
    }

    /// <summary>Tuiles courantes (pour recompute KbisSummary par instance).</summary>
    public IReadOnlyList<KbisLegacyScenarioAdapter> Items => _scenarios;

    /// <summary>Play par-scénario (tuile ▶) : (re)lance CE scénario seul. Wiré dans le ctor.</summary>
    public ICommand PlayScenarioCommand { get; }

    /// <summary>Stop par-scénario (tuile ⏹) : taskkill le worker de la tuile. Wiré dans le ctor.</summary>
    public ICommand StopScenarioCommand { get; }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // La source manifest a changé (reload) → rebuild avec le plan SUITE.
        Rebuild(_vm.BuildKbisSuitePlan());
    }
}
